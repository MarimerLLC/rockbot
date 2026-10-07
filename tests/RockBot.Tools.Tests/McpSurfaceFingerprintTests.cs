using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

/// <summary>
/// Pins what counts as a surface change (mcp-aggregator#41): descriptions and schemas both move
/// the fingerprint, key order and input order don't, and a null schema isn't <c>{}</c>.
/// </summary>
[TestClass]
public class McpSurfaceFingerprintTests
{
    private const string Schema = """{"type":"object","properties":{"to":{"type":"string"},"cc":{"type":"array"}},"required":["to"]}""";
    private const string SchemaReordered = """{ "required": ["to"], "properties": { "cc": {"type":"array"}, "to": {"type":"string"} }, "type": "object" }""";

    private static string ServerOf(params (string Name, string? Description, string? Schema)[] tools) =>
        McpSurfaceFingerprint.Server(tools, []);

    [TestMethod]
    public void DescriptionOnlyChange_MovesTheFingerprint()
    {
        Assert.AreNotEqual(
            McpSurfaceFingerprint.Tool("send_email", "Sends an email.", Schema),
            McpSurfaceFingerprint.Tool("send_email", "Sends an email as the signed-in user.", Schema));
    }

    [TestMethod]
    public void SchemaOnlyChange_MovesTheFingerprint()
    {
        Assert.AreNotEqual(
            McpSurfaceFingerprint.Tool("send_email", "d", Schema),
            McpSurfaceFingerprint.Tool("send_email", "d", Schema.Replace("\"required\":[\"to\"]", "\"required\":[\"to\",\"cc\"]")));
    }

    [TestMethod]
    public void ReorderedSchemaKeys_DoNotMoveTheFingerprint()
    {
        Assert.AreEqual(
            McpSurfaceFingerprint.Tool("send_email", "d", Schema),
            McpSurfaceFingerprint.Tool("send_email", "d", SchemaReordered));
    }

    [TestMethod]
    public void ArrayOrder_IsSignificant()
    {
        Assert.AreNotEqual(
            McpSurfaceFingerprint.Tool("t", "d", """{"enum":["a","b"]}"""),
            McpSurfaceFingerprint.Tool("t", "d", """{"enum":["b","a"]}"""));
    }

    [TestMethod]
    public void NullSchema_DiffersFromEmptyObject()
    {
        Assert.AreNotEqual(
            McpSurfaceFingerprint.Tool("t", "d", null),
            McpSurfaceFingerprint.Tool("t", "d", "{}"));
    }

    [TestMethod]
    public void ServerFingerprint_IgnoresToolOrder()
    {
        Assert.AreEqual(
            ServerOf(("a", "x", Schema), ("b", "y", null)),
            ServerOf(("b", "y", null), ("a", "x", Schema)));
    }

    [TestMethod]
    public void ServerFingerprint_MovesWhenAToolIsAdded()
    {
        Assert.AreNotEqual(
            ServerOf(("a", "x", Schema)),
            ServerOf(("a", "x", Schema), ("b", "y", null)));
    }

    [TestMethod]
    public void ServerFingerprint_CoversPromptArguments()
    {
        McpPromptDefinition Briefing(bool dateRequired) => new()
        {
            Name = "daily_briefing",
            Description = "Brief me.",
            Arguments = [new McpPromptArgument { Name = "date", Required = dateRequired }]
        };

        Assert.AreNotEqual(
            McpSurfaceFingerprint.Server([], [Briefing(true)]),
            McpSurfaceFingerprint.Server([], [Briefing(false)]));
    }

    [TestMethod]
    public void ToolsAndPrompts_AreSeparateSections()
    {
        // A tool and a prompt with identical text must not collide.
        Assert.AreNotEqual(
            McpSurfaceFingerprint.Server([("x", "d", null)], []),
            McpSurfaceFingerprint.Server([], [new McpPromptDefinition { Name = "x", Description = "d" }]));
    }

    [TestMethod]
    public void Fingerprint_IsFullLengthLowercaseHex()
    {
        var fingerprint = McpSurfaceFingerprint.Tool("t", "d", Schema);

        Assert.AreEqual(64, fingerprint.Length);
        Assert.IsTrue(fingerprint.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'), fingerprint);
    }
}

[TestClass]
public class McpServerNamesTests
{
    [TestMethod]
    [DataRow("adjutant")]
    [DataRow("onedrive-marimer")]
    [DataRow("todo_mcp")]
    [DataRow("microsoft.learn")]
    [DataRow("A1")]
    public void PlainNames_AreValid(string name) => Assert.IsNull(McpServerNames.Validate(name));

    [TestMethod]
    [DataRow("")]
    [DataRow("-leading")]
    [DataRow("_leading")]
    [DataRow("has space")]
    [DataRow("slash/name")]
    [DataRow("adjutant__mail")]
    public void OtherNames_AreRejectedWithAReason(string name) => Assert.IsNotNull(McpServerNames.Validate(name));

    [TestMethod]
    public void NamesLongerThan64_AreRejected()
    {
        Assert.IsNull(McpServerNames.Validate(new string('a', 64)));
        Assert.IsNotNull(McpServerNames.Validate(new string('a', 65)));
    }

    [TestMethod]
    public void NewId_IsTwelveHexCharactersAndUnique()
    {
        var a = McpServerNames.NewId();
        var b = McpServerNames.NewId();

        Assert.AreEqual(12, a.Length);
        Assert.IsTrue(a.All(Uri.IsHexDigit), a);
        Assert.AreNotEqual(a, b);
    }
}

[TestClass]
public class McpSchemaInvalidationTests
{
    private static McpServerSummary Summary(string? fingerprint, string? id = "abc123def456") =>
        new() { ServerName = "adjutant", Fingerprint = fingerprint, ServerId = id };

    [TestMethod]
    public void SameFingerprintAndId_IsUnchanged() =>
        Assert.IsTrue(McpServersIndexedHandler.SurfaceUnchanged(Summary("f1"), Summary("f1")));

    [TestMethod]
    public void MovedFingerprint_IsAChange() =>
        Assert.IsFalse(McpServersIndexedHandler.SurfaceUnchanged(Summary("f1"), Summary("f2")));

    [TestMethod]
    public void UnknownFingerprint_IsTreatedAsAChange()
    {
        Assert.IsFalse(McpServersIndexedHandler.SurfaceUnchanged(Summary("f1"), Summary(null)));
        Assert.IsFalse(McpServersIndexedHandler.SurfaceUnchanged(Summary(null), Summary("f1")));
    }

    [TestMethod]
    public void NewServer_IsAChange() =>
        Assert.IsFalse(McpServersIndexedHandler.SurfaceUnchanged(null, Summary("f1")));

    [TestMethod]
    public void SameNameWithADifferentId_IsAChange() =>
        Assert.IsFalse(McpServersIndexedHandler.SurfaceUnchanged(Summary("f1", "aaaaaaaaaaaa"), Summary("f1", "bbbbbbbbbbbb")));
}
