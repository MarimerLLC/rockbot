using System.Text.Json;
using RockBot.Agent.McpBridge.ArgGuards;

namespace RockBot.Agent.Tests.McpBridge.ArgGuards;

[TestClass]
public class PathPrefixArgGuardTests
{
    private static readonly PathPrefixArgGuard Guard = new();

    private static JsonElement Options(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement DefaultOptions(bool requireArgs = false) =>
        Options($$"""
            { "args": ["save_directory"], "allowedPrefixes": ["/rockbot/shared"], "requireArgs": {{(requireArgs ? "true" : "false")}} }
            """);

    private static McpArgGuardContext Context(
        Dictionary<string, object?> args, JsonElement? options = null) =>
        new("onedrive-personal", "download_file", args, options ?? DefaultOptions());

    private static async Task<McpArgGuardResult> Apply(
        Dictionary<string, object?> args, JsonElement? options = null) =>
        await Guard.ApplyAsync(Context(args, options), CancellationToken.None);

    // ── Apply: accept/reject ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Apply_PathInsidePrefix_Allows()
    {
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared/downloads" });
        Assert.IsFalse(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_ExactPrefixPath_Allows()
    {
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared" });
        Assert.IsFalse(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_PathOutsidePrefix_RejectsNamingArgAndPrefixes()
    {
        var result = await Apply(new() { ["save_directory"] = "/tmp" });
        Assert.IsTrue(result.IsRejected);
        StringAssert.Contains(result.RejectionMessage, "save_directory");
        StringAssert.Contains(result.RejectionMessage, "/rockbot/shared");
        StringAssert.Contains(result.RejectionMessage, "pod-local");
    }

    [TestMethod]
    public async Task Apply_TraversalEscapingPrefix_Rejects()
    {
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared/../../tmp" });
        Assert.IsTrue(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_TraversalStayingInside_Allows()
    {
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared/a/../b" });
        Assert.IsFalse(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_RelativePath_Rejects()
    {
        var result = await Apply(new() { ["save_directory"] = "downloads" });
        Assert.IsTrue(result.IsRejected);
        StringAssert.Contains(result.RejectionMessage, "relative");
    }

    [TestMethod]
    public async Task Apply_SiblingPrefix_Rejects()
    {
        // Boundary check: "/rockbot/shared" must not match "/rockbot/shared-evil".
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared-evil/x" });
        Assert.IsTrue(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_PrefixWithTrailingSlash_NormalizedAndAllows()
    {
        var options = Options("""
            { "args": ["save_directory"], "allowedPrefixes": ["/rockbot/shared/"] }
            """);
        var result = await Apply(new() { ["save_directory"] = "/rockbot/shared/x" }, options);
        Assert.IsFalse(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_CaseMismatch_Rejects()
    {
        // Ordinal comparison — the target filesystem is the Linux pod.
        var result = await Apply(new() { ["save_directory"] = "/Rockbot/Shared/x" });
        Assert.IsTrue(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_BackslashSeparators_NormalizedBeforeCheck()
    {
        var inside = await Apply(new() { ["save_directory"] = "\\rockbot\\shared\\x" });
        Assert.IsFalse(inside.IsRejected);

        var escape = await Apply(new() { ["save_directory"] = "/rockbot/shared/..\\..\\tmp" });
        Assert.IsTrue(escape.IsRejected);
    }

    [TestMethod]
    public async Task Apply_MissingArg_Allows()
    {
        var result = await Apply(new() { ["remote_path"] = "Apps/RockBot/file.json" });
        Assert.IsFalse(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_MissingArgWithRequireArgs_Rejects()
    {
        var result = await Apply(
            new() { ["remote_path"] = "Apps/RockBot/file.json" },
            DefaultOptions(requireArgs: true));
        Assert.IsTrue(result.IsRejected);
        StringAssert.Contains(result.RejectionMessage, "save_directory");
        StringAssert.Contains(result.RejectionMessage, "required");
    }

    [TestMethod]
    public async Task Apply_ArgNameCaseInsensitive_Matches()
    {
        var result = await Apply(new() { ["Save_Directory"] = "/tmp" });
        Assert.IsTrue(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_NonStringArg_Rejects()
    {
        var result = await Apply(new() { ["save_directory"] = 42 });
        Assert.IsTrue(result.IsRejected);
    }

    [TestMethod]
    public async Task Apply_MultipleArgsOneOutside_Rejects()
    {
        var options = Options("""
            { "args": ["save_directory", "output_dir"], "allowedPrefixes": ["/rockbot/shared"] }
            """);
        var result = await Apply(new()
        {
            ["save_directory"] = "/rockbot/shared/ok",
            ["output_dir"] = "/var/data"
        }, options);
        Assert.IsTrue(result.IsRejected);
        StringAssert.Contains(result.RejectionMessage, "output_dir");
    }

    [TestMethod]
    public async Task Apply_MultiplePrefixes_AnyMatchAllows()
    {
        var options = Options("""
            { "args": ["save_directory"], "allowedPrefixes": ["/rockbot/shared", "/data/agent"] }
            """);
        var result = await Apply(new() { ["save_directory"] = "/data/agent/scratch" }, options);
        Assert.IsFalse(result.IsRejected);
    }

    // ── ValidateOptions ───────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateOptions_MissingOptions_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Guard.ValidateOptions(null));
    }

    [TestMethod]
    public void ValidateOptions_EmptyArgs_Throws()
    {
        var options = Options("""{ "args": [], "allowedPrefixes": ["/rockbot/shared"] }""");
        Assert.ThrowsExactly<InvalidOperationException>(() => Guard.ValidateOptions(options));
    }

    [TestMethod]
    public void ValidateOptions_EmptyAllowedPrefixes_Throws()
    {
        var options = Options("""{ "args": ["save_directory"], "allowedPrefixes": [] }""");
        Assert.ThrowsExactly<InvalidOperationException>(() => Guard.ValidateOptions(options));
    }

    [TestMethod]
    public void ValidateOptions_RelativePrefix_Throws()
    {
        var options = Options("""{ "args": ["save_directory"], "allowedPrefixes": ["shared"] }""");
        Assert.ThrowsExactly<InvalidOperationException>(() => Guard.ValidateOptions(options));
    }

    [TestMethod]
    public void ValidateOptions_PascalCaseKeys_Binds()
    {
        var options = Options("""{ "Args": ["save_directory"], "AllowedPrefixes": ["/rockbot/shared"] }""");
        Guard.ValidateOptions(options); // should not throw
    }

    [TestMethod]
    public void ValidateOptions_ValidOptions_DoesNotThrow()
    {
        Guard.ValidateOptions(DefaultOptions());
    }

    // ── NormalizePath internals ───────────────────────────────────────────────

    [TestMethod]
    public void NormalizePath_TraversalAboveRoot_ReturnsNull()
    {
        Assert.IsNull(PathPrefixArgGuard.NormalizePath("/../etc"));
    }

    [TestMethod]
    public void NormalizePath_DuplicateSeparators_Collapsed()
    {
        Assert.AreEqual("/rockbot/shared/x", PathPrefixArgGuard.NormalizePath("/rockbot//shared///x"));
    }

    [TestMethod]
    public void NormalizePath_DotSegments_Removed()
    {
        Assert.AreEqual("/rockbot/shared/x", PathPrefixArgGuard.NormalizePath("/rockbot/./shared/./x"));
    }

    [TestMethod]
    public void IsUnderPrefix_RootPrefix_MatchesEverything()
    {
        Assert.IsTrue(PathPrefixArgGuard.IsUnderPrefix("/anything", "/"));
    }
}
