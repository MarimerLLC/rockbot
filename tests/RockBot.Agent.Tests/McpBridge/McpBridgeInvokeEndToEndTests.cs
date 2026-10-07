using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// End-to-end coverage of the bridge's invoke and prompt paths against a real MCP server: what the
/// model gets back for unknown names, operator-filtered tools, server-side failures, and argument
/// mistakes. See <see cref="BridgeHarness"/>.
/// </summary>
[TestClass]
public class McpBridgeInvokeEndToEndTests
{
    private static McpServerTool MoveEmail() => McpServerTool.Create(
        (string emailId, string destination) => emailId switch
        {
            "graph-bug" => throw new McpException("ErrorInvalidIdMalformed: Id is malformed."),
            "crash" => throw new InvalidOperationException("internal detail"),
            _ => $"moved {emailId} to {destination}",
        },
        new McpServerToolCreateOptions { Name = "move_email" });

    private static McpServerTool DeleteEverything() => McpServerTool.Create(
        () => "deleted",
        new McpServerToolCreateOptions { Name = "delete_everything" });

    private static McpServerPrompt DailyBriefing() => McpServerPrompt.Create(
        (string date, string? accountId = null) => $"Brief me on {date} for {accountId ?? "all accounts"}",
        new McpServerPromptCreateOptions { Name = "daily_briefing" });

    private static Task<BridgeHarness> StartAsync(Action<RockBot.Agent.McpBridge.McpBridgeServerConfig>? configure = null) =>
        BridgeHarness.StartAsync([MoveEmail(), DeleteEverything()], [DailyBriefing()], configure);

    [TestMethod]
    public async Task ValidCall_ReturnsTheDownstreamResult()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", """{"emailId":"1","destination":"archive"}""");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("moved 1 to archive", response.Content);
    }

    // ── Unknown names ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task UnknownTool_ListsTheServersTools()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("no_such_tool", "{}");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Unknown tool 'no_such_tool' on server 'fixture'");
        StringAssert.Contains(response.Content, "move_email");
    }

    [TestMethod]
    public async Task UnknownServer_ListsTheRegisteredServers()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", "{}", server: "calendar");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Unknown MCP server 'calendar'");
        StringAssert.Contains(response.Content, "Registered servers: [fixture]");
    }

    [TestMethod]
    public async Task OperatorDeniedTool_CannotBeCalledByName()
    {
        await using var harness = await StartAsync(c => c.DeniedTools = ["delete_everything"]);

        var response = await harness.InvokeAsync("delete_everything", "{}");

        Assert.IsTrue(response.IsError);
        Assert.IsFalse(response.Content!.Contains("deleted", StringComparison.Ordinal), response.Content);
        StringAssert.Contains(response.Content, "Unknown tool 'delete_everything'");
        Assert.IsFalse(response.Content.Contains("[delete_everything", StringComparison.Ordinal),
            "A denied tool must not be offered as an alternative.");
    }

    [TestMethod]
    public async Task ToolOutsideAllowList_CannotBeCalledByName()
    {
        await using var harness = await StartAsync(c => c.AllowedTools = ["move_email"]);

        var response = await harness.InvokeAsync("delete_everything", "{}");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Available tools: [move_email]");
    }

    // ── Downstream failures: positive evidence only ──────────────────────────

    [TestMethod]
    public async Task RuntimeErrorWithDetail_PassesThroughWithoutArgumentHint()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", """{"emailId":"graph-bug","destination":"archive"}""");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "ErrorInvalidIdMalformed");
        Assert.IsFalse(response.Content!.Contains("Input schema", StringComparison.Ordinal), response.Content);
        Assert.IsFalse(response.Content.Contains("declared as", StringComparison.Ordinal), response.Content);
    }

    [TestMethod]
    public async Task ExceptionWithoutDetail_GetsAHedgedNote()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", """{"emailId":"crash","destination":"archive"}""");

        Assert.IsTrue(response.IsError);
        Assert.IsFalse(response.Content!.Contains("internal detail", StringComparison.Ordinal),
            "The SDK hides plain exception messages; the hint must not imply otherwise.");
        StringAssert.Contains(response.Content, "gave no detail");
    }

    [TestMethod]
    public async Task IntegerForStringParameter_NamesTheParameterAndTypes()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", """{"emailId":12345,"destination":"archive"}""");

        Assert.IsTrue(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "Parameter 'emailId' is declared as string but you sent an integer.");
    }

    [TestMethod]
    public async Task WrongKey_NamesMissingAndUnrecognisedKeys()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("move_email", """{"emailId":"1","folder":"archive"}""");

        Assert.IsTrue(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "Missing required parameter(s): [destination]");
        StringAssert.Contains(response.Content, "Unrecognized argument key(s): [folder]");
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Prompt_WithRequiredArguments_ReturnsMessages()
    {
        await using var harness = await StartAsync();

        var response = await harness.GetPromptAsync("daily_briefing", new() { ["date"] = "2026-10-06" });

        Assert.IsNull(response.Error, response.Error);
        StringAssert.Contains(response.Messages.Single().Content, "Brief me on 2026-10-06");
    }

    [TestMethod]
    public async Task Prompt_MissingRequiredArgument_IsAnsweredWithTheSignature()
    {
        await using var harness = await StartAsync();

        var response = await harness.GetPromptAsync("daily_briefing", new() { ["accountId"] = "work" });

        Assert.IsNotNull(response.Error);
        StringAssert.Contains(response.Error, "missing required argument(s): [date]");
        StringAssert.Contains(response.Error, "accountId (optional)");
    }

    [TestMethod]
    public async Task UnknownPrompt_ListsTheServersPrompts()
    {
        await using var harness = await StartAsync();

        var response = await harness.GetPromptAsync("briefing");

        Assert.IsNotNull(response.Error);
        StringAssert.Contains(response.Error, "Available prompts: [daily_briefing]");
    }

    [TestMethod]
    public async Task Prompt_OnUnknownServer_ListsTheRegisteredServers()
    {
        await using var harness = await StartAsync();

        var response = await harness.GetPromptAsync("daily_briefing", server: "calendar");

        StringAssert.Contains(response.Error, "Registered servers: [fixture]");
    }
}
