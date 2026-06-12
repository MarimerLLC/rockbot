using System.Text.Json;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.ArgGuards;

namespace RockBot.Agent.Tests.McpBridge.ArgGuards;

[TestClass]
public class McpArgGuardEvaluatorTests
{
    private sealed class FakeGuard : IMcpArgGuard
    {
        public int ApplyCount;
        public Func<McpArgGuardContext, McpArgGuardResult> OnApply { get; init; } =
            _ => McpArgGuardResult.Allowed;

        public void ValidateOptions(JsonElement? options) { }

        public ValueTask<McpArgGuardResult> ApplyAsync(McpArgGuardContext context, CancellationToken ct)
        {
            ApplyCount++;
            return ValueTask.FromResult(OnApply(context));
        }
    }

    private sealed class ThrowingGuard : IMcpArgGuard
    {
        public void ValidateOptions(JsonElement? options) =>
            throw new InvalidOperationException("bad options");
        public ValueTask<McpArgGuardResult> ApplyAsync(McpArgGuardContext context, CancellationToken ct) =>
            throw new InvalidOperationException("guard exploded");
    }

    private static McpArgGuardRegistry Registry(params (string Name, IMcpArgGuard Guard)[] guards) =>
        new(guards.Select(g => new McpArgGuardRegistration(g.Name, g.Guard)));

    private static McpBridgeServerConfig Config(params McpArgGuardConfig[] guards) =>
        new() { Type = "sse", Url = "http://test/", ArgGuards = [.. guards] };

    private static Task<string?> Evaluate(
        IMcpArgGuardRegistry? registry,
        McpBridgeServerConfig config,
        string toolName = "download_file",
        Dictionary<string, object?>? args = null) =>
        McpArgGuardEvaluator.EvaluateAsync(
            registry, "test-server", config, toolName, args ?? [], CancellationToken.None).AsTask();

    // ── EvaluateAsync ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Evaluate_NoGuards_ReturnsNull()
    {
        var result = await Evaluate(Registry(), Config());
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Evaluate_ToolNotInRuleTools_SkipsRule()
    {
        var guard = new FakeGuard();
        var config = Config(new McpArgGuardConfig { Handler = "g", Tools = ["other_tool"] });
        var result = await Evaluate(Registry(("g", guard)), config);
        Assert.IsNull(result);
        Assert.AreEqual(0, guard.ApplyCount);
    }

    [TestMethod]
    public async Task Evaluate_EmptyToolsList_AppliesToAllTools()
    {
        var guard = new FakeGuard();
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        await Evaluate(Registry(("g", guard)), config, toolName: "anything");
        Assert.AreEqual(1, guard.ApplyCount);
    }

    [TestMethod]
    public async Task Evaluate_ToolNameCaseInsensitive_Matches()
    {
        var guard = new FakeGuard();
        var config = Config(new McpArgGuardConfig { Handler = "g", Tools = ["Download_File"] });
        await Evaluate(Registry(("g", guard)), config, toolName: "download_file");
        Assert.AreEqual(1, guard.ApplyCount);
    }

    [TestMethod]
    public async Task Evaluate_UnknownHandler_FailsClosedWithMessage()
    {
        var config = Config(new McpArgGuardConfig { Handler = "nope" });
        var result = await Evaluate(Registry(), config);
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "nope");
        StringAssert.Contains(result, "fail closed");
    }

    [TestMethod]
    public async Task Evaluate_NullRegistryWithGuards_FailsClosed()
    {
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        var result = await Evaluate(null, config);
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "fail closed");
    }

    [TestMethod]
    public async Task Evaluate_GuardThrows_FailsClosedWithMessage()
    {
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        var result = await Evaluate(Registry(("g", new ThrowingGuard())), config);
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "guard exploded");
    }

    [TestMethod]
    public async Task Evaluate_FirstRejectionShortCircuits_SecondGuardNotInvoked()
    {
        var first = new FakeGuard { OnApply = _ => McpArgGuardResult.Reject("first says no") };
        var second = new FakeGuard();
        var config = Config(
            new McpArgGuardConfig { Handler = "first" },
            new McpArgGuardConfig { Handler = "second" });

        var result = await Evaluate(Registry(("first", first), ("second", second)), config);

        Assert.AreEqual("first says no", result);
        Assert.AreEqual(0, second.ApplyCount);
    }

    [TestMethod]
    public async Task Evaluate_GuardMutatesArguments_MutationVisibleToCaller()
    {
        // Pins the by-reference contract: guards receive the live dictionary the
        // bridge forwards, so future transforming handlers work without plumbing.
        var guard = new FakeGuard
        {
            OnApply = ctx =>
            {
                ctx.Arguments["normalized"] = true;
                return McpArgGuardResult.Allowed;
            }
        };
        var args = new Dictionary<string, object?>();
        var config = Config(new McpArgGuardConfig { Handler = "g" });

        await Evaluate(Registry(("g", guard)), config, args: args);

        Assert.IsTrue((bool)args["normalized"]!);
    }

    // ── ValidateConfig ────────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateConfig_NoGuards_ReturnsNull()
    {
        Assert.IsNull(McpArgGuardEvaluator.ValidateConfig(Registry(), "s", Config()));
    }

    [TestMethod]
    public void ValidateConfig_Valid_ReturnsNull()
    {
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        Assert.IsNull(McpArgGuardEvaluator.ValidateConfig(Registry(("g", new FakeGuard())), "s", config));
    }

    [TestMethod]
    public void ValidateConfig_UnknownHandler_ReturnsError()
    {
        var config = Config(new McpArgGuardConfig { Handler = "nope" });
        var error = McpArgGuardEvaluator.ValidateConfig(Registry(("g", new FakeGuard())), "s", config);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "nope");
        StringAssert.Contains(error, "g"); // lists known handlers
    }

    [TestMethod]
    public void ValidateConfig_MissingHandlerName_ReturnsError()
    {
        var config = Config(new McpArgGuardConfig { Handler = null });
        var error = McpArgGuardEvaluator.ValidateConfig(Registry(), "s", config);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "handler");
    }

    [TestMethod]
    public void ValidateConfig_NullRegistryWithGuards_ReturnsError()
    {
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        Assert.IsNotNull(McpArgGuardEvaluator.ValidateConfig(null, "s", config));
    }

    [TestMethod]
    public void ValidateConfig_OptionsValidationThrows_ReturnsError()
    {
        var config = Config(new McpArgGuardConfig { Handler = "g" });
        var error = McpArgGuardEvaluator.ValidateConfig(Registry(("g", new ThrowingGuard())), "s", config);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "bad options");
    }
}
