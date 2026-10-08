using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

/// <summary>
/// Wisps on typed <c>{server}__{tool}</c> MCP tools (#647): direct steps call the wrapper, the
/// typed-name shorthand resolves through the directory (never by parsing), LLM steps get typed
/// tools that the agent loop keeps, and <c>mcp_invoke_tool</c> stays only as the fallback for a
/// tool with no wrapper.
/// </summary>
[TestClass]
public class WispTypedMcpTests
{
    private const string EventsSchema =
        """{"type":"object","properties":{"accountId":{"type":"string"},"timeZone":{"type":"string"}},"required":["accountId","timeZone"]}""";

    // ── GatewayRouter ────────────────────────────────────────────────────────

    [TestMethod]
    public void Route_TypedEntry_CallsTheWrapperWithTheToolsOwnArguments()
    {
        var directory = Directory();
        var step = McpStep(server: "calendar-mcp", tool: "get_events", paramsJson: """{"accountId":"xebia"}""");

        var route = GatewayRouter.Route(step, "w", new Dictionary<string, WispStepResult>(),
            directory.Resolve("calendar-mcp", "get_events"));

        Assert.IsTrue(route.IsSuccess);
        Assert.AreEqual("calendar-mcp__get_events", route.ToolName);
        Assert.AreEqual("xebia", JsonDocument.Parse(route.Arguments!).RootElement.GetProperty("accountId").GetString(),
            "a typed tool gets its arguments unwrapped, not inside server_name/tool_name/arguments");
    }

    [TestMethod]
    public void Route_NoTypedEntry_FallsBackToMcpInvokeTool()
    {
        var route = GatewayRouter.Route(McpStep(server: "calendar-mcp", tool: "get_events"), "w",
            new Dictionary<string, WispStepResult>());

        Assert.AreEqual("mcp_invoke_tool", route.ToolName);
        var args = JsonDocument.Parse(route.Arguments!).RootElement;
        Assert.AreEqual("calendar-mcp", args.GetProperty("server_name").GetString());
        Assert.AreEqual("get_events", args.GetProperty("tool_name").GetString());
    }

    [TestMethod]
    public void Route_ShorthandWithoutAWrapper_IsAnAuthoringError()
    {
        var route = GatewayRouter.Route(McpStep(server: null, tool: "calendar-mcp__get_events"), "w",
            new Dictionary<string, WispStepResult>());

        Assert.IsFalse(route.IsSuccess);
        StringAssert.Contains(route.ErrorMessage, "typed tool name");
    }

    // ── Direct steps ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DirectStep_LongForm_InvokesTheWrapper()
    {
        var directory = Directory();
        var registry = new FakeToolRegistry();
        RegisterInvokeTool(registry, out var invokeCalls);
        var executor = NewExecutor(registry, directory);

        var result = await executor.ExecuteAsync(
            Wisp(McpStep("calendar-mcp", "get_events", """{"accountId":"xebia","timeZone":"UTC"}""")), "w1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        var call = directory.WrapperCalls.Single();
        Assert.AreEqual("calendar-mcp__get_events", call.ToolName);
        Assert.AreEqual("xebia", JsonDocument.Parse(call.Arguments!).RootElement.GetProperty("accountId").GetString());
        Assert.AreEqual(0, invokeCalls.Count, "mcp_invoke_tool isn't used when a wrapper exists");
    }

    [TestMethod]
    public async Task DirectStep_Shorthand_ResolvesThroughTheDirectory()
    {
        var directory = Directory();
        var executor = NewExecutor(new FakeToolRegistry(), directory);

        var result = await executor.ExecuteAsync(
            Wisp(McpStep(server: null, tool: "calendar-mcp__get_events", paramsJson: """{"accountId":"x","timeZone":"UTC"}""")),
            "w2", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        Assert.AreEqual("calendar-mcp__get_events", directory.WrapperCalls.Single().ToolName);
    }

    [TestMethod]
    public async Task DirectStep_UnknownShorthand_FailsStructural_AfterTheReadinessWait()
    {
        var executor = NewExecutor(new FakeToolRegistry(), Directory(), readinessWait: TimeSpan.FromMilliseconds(250));

        var result = await executor.ExecuteAsync(
            Wisp(McpStep(server: null, tool: "calendar-mcp__no_such_tool")), "w3", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        var error = result.StepResults[0].Error!;
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "isn't a typed MCP tool");
    }

    [TestMethod]
    public async Task DirectStep_ShorthandIndexedDuringTheWait_Succeeds()
    {
        var directory = Directory();
        directory.HideWrappersFor = 2; // the first lookups miss, as before the first index message
        var executor = NewExecutor(new FakeToolRegistry(), directory, readinessWait: TimeSpan.FromSeconds(5));

        var result = await executor.ExecuteAsync(
            Wisp(McpStep(server: null, tool: "calendar-mcp__get_events", paramsJson: """{"accountId":"x","timeZone":"UTC"}""")),
            "w4", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
    }

    [TestMethod]
    public async Task DirectStep_ToolWithoutAWrapper_FallsBackToMcpInvokeTool()
    {
        var directory = Directory();
        var registry = new FakeToolRegistry();
        RegisterInvokeTool(registry, out var invokeCalls);
        var executor = NewExecutor(registry, directory);

        var result = await executor.ExecuteAsync(
            Wisp(McpStep("calendar-mcp", "a_tool_with_a_very_long_name_that_has_no_wrapper")), "w5", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        Assert.AreEqual(0, directory.WrapperCalls.Count);
        var args = JsonDocument.Parse(invokeCalls.Single().Arguments!).RootElement;
        Assert.AreEqual("a_tool_with_a_very_long_name_that_has_no_wrapper", args.GetProperty("tool_name").GetString());
    }

    [TestMethod]
    public async Task DirectStep_TypedFailure_NamesTheTypedTool()
    {
        var directory = Directory(wrapperError: "accountId is required");
        var executor = NewExecutor(new FakeToolRegistry(), directory);

        var result = await executor.ExecuteAsync(
            Wisp(McpStep("calendar-mcp", "get_events", """{"accountId":"x","timeZone":"UTC"}""")), "w6", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("calendar-mcp__get_events", result.StepResults[0].Error!.ToolName);
    }

    [TestMethod]
    public async Task DirectStep_Typed_PreflightDefaultsStillFillMissingFields()
    {
        var directory = Directory();
        var preflight = new FillingPreflight(EventsSchema, new Dictionary<string, object?> { ["timeZone"] = "America/Chicago" });
        var executor = NewExecutor(new FakeToolRegistry(), directory, preflight: preflight);

        var result = await executor.ExecuteAsync(
            Wisp(McpStep("calendar-mcp", "get_events", """{"accountId":"xebia"}""")), "w7", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.StepResults[0].Error?.Message);
        var args = JsonDocument.Parse(directory.WrapperCalls.Single().Arguments!).RootElement;
        Assert.AreEqual("America/Chicago", args.GetProperty("timeZone").GetString());
    }

    // ── LLM step tool scope ──────────────────────────────────────────────────

    [TestMethod]
    public void LlmStepTools_AreTypedAndCallerScoped()
    {
        var directory = Directory();
        var registry = new FakeToolRegistry();
        RegisterInvokeTool(registry, out _);
        registry.Register(new ToolRegistration { Name = "web_browse", Description = "web", Source = "web" }, new FakeToolExecutor("ok"));
        var executor = NewExecutor(registry, directory);

        var definition = new WispDefinition
        {
            Description = "d",
            Tools = ["web_browse", "ms365__search_emails"],
            Steps = [McpStep("calendar-mcp", "get_events"), LlmStep()]
        };

        var tools = executor.BuildLlmStepTools(definition, "wisp/ns", "parent");
        var names = tools.Select(t => t.Name).ToList();

        CollectionAssert.Contains(names, "calendar-mcp__get_events");
        CollectionAssert.Contains(names, "ms365__search_emails");
        CollectionAssert.Contains(names, "web_browse");
        CollectionAssert.DoesNotContain(names, "mcp_invoke_tool", "every MCP tool here has a wrapper");
        Assert.IsTrue(tools.Where(t => names.Contains(t.Name) && t.Name.Contains("__"))
            .All(t => t.GetService<ICallerScopedTool>() is not null));
    }

    [TestMethod]
    public void LlmStepTools_ServerEntry_BringsInThatServersTypedTools()
    {
        var executor = NewExecutor(new FakeToolRegistry(), Directory());

        var tools = executor.BuildLlmStepTools(
            new WispDefinition { Description = "d", Tools = ["calendar-mcp"], Steps = [LlmStep()] }, "wisp/ns", null);
        var names = tools.Select(t => t.Name).ToList();

        CollectionAssert.Contains(names, "calendar-mcp__get_events");
        CollectionAssert.Contains(names, "calendar-mcp__list_accounts");
        CollectionAssert.DoesNotContain(names, "ms365__search_emails");
    }

    [TestMethod]
    public void LlmStepTools_ToolWithoutAWrapper_AddsMcpInvokeTool()
    {
        var registry = new FakeToolRegistry();
        RegisterInvokeTool(registry, out _);
        var executor = NewExecutor(registry, Directory());

        var tools = executor.BuildLlmStepTools(new WispDefinition
        {
            Description = "d",
            Steps = [McpStep("calendar-mcp", "a_tool_with_a_very_long_name_that_has_no_wrapper"), LlmStep()]
        }, "wisp/ns", null);

        CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "mcp_invoke_tool");
    }

    [TestMethod]
    public void LlmStepTools_WithoutADirectory_KeepTodaysMcpInvokeTool()
    {
        var registry = new FakeToolRegistry();
        RegisterInvokeTool(registry, out _);
        var executor = NewExecutor(registry, directory: null);

        var tools = executor.BuildLlmStepTools(new WispDefinition
        {
            Description = "d",
            Steps = [McpStep("calendar-mcp", "get_events"), LlmStep()]
        }, "wisp/ns", null);

        CollectionAssert.Contains(tools.Select(t => t.Name).ToList(), "mcp_invoke_tool");
    }

    // ── Canonical shorthand and hashing ──────────────────────────────────────

    [TestMethod]
    public void Shorthand_AndLongForm_ShareAShapeHash_AfterCanonicalising()
    {
        var directory = Directory();
        var shorthand = McpStepResolver.CanonicalizeShorthand(Wisp(McpStep(null, "calendar-mcp__get_events")), directory);
        var longForm = Wisp(McpStep("calendar-mcp", "get_events"));

        Assert.AreEqual("calendar-mcp", shorthand.Steps[0].Server);
        Assert.AreEqual("get_events", shorthand.Steps[0].Tool);
        Assert.AreEqual(SpawnWispsExecutor.ComputeShapeHash(longForm), SpawnWispsExecutor.ComputeShapeHash(shorthand));
    }

    [TestMethod]
    public void UnresolvableShorthand_IsLeftAsWritten()
    {
        var definition = Wisp(McpStep(null, "nobody__knows"));
        Assert.AreSame(definition, McpStepResolver.CanonicalizeShorthand(definition, Directory()));
    }

    // ── Spawn: canonical bodies and fingerprints on eager promotion ───────────

    [TestMethod]
    public async Task Spawn_Shorthand_IsLoggedCanonical_AndEagerPromotionRecordsToolFingerprints()
    {
        var directory = Directory();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions();
        var wispExecutor = new WispExecutor(new FakeToolRegistry(), memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance, mcpToolDirectory: directory);
        var log = new TrackingWispExecutionLog();
        var skills = new InMemorySkillStore();
        var usage = new InMemorySkillUsageStore();
        var spawn = new SpawnWispsExecutor(wispExecutor, log, feedbackStore: null, memory, options,
            NullLogger<SpawnWispsExecutor>.Instance, skillStore: skills, skillUsageStore: usage,
            mcpToolDirectory: directory);

        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("patrol/heartbeat-patrol", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        for (var i = 0; i < 2; i++)
        {
            var response = await spawn.ExecuteAsync(new ToolInvokeRequest
            {
                ToolCallId = $"c{i}",
                ToolName = "spawn_wisps",
                SessionId = "patrol/heartbeat-patrol",
                Arguments = """
                    { "definitions": [ { "description": "Events",
                      "steps": [ { "id": "e", "mode": "Direct", "gateway": "Mcp",
                                   "tool": "calendar-mcp__get_events",
                                   "params": { "accountId": "xebia", "timeZone": "UTC" } } ] } ] }
                    """
            }, CancellationToken.None);
            Assert.IsFalse(response.IsError, response.Content);
        }
        await Task.Delay(150);

        StringAssert.Contains(log.Records[0].DefinitionBody, "\"server\":\"calendar-mcp\"",
            "the shorthand is stored as server + tool");
        var entry = (await skills.GetAsync("calendar/scan"))!.Manifest!.Single();
        Assert.AreEqual("fp-events", entry.ToolFingerprints!["calendar-mcp/get_events"]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static FakeMcpToolDirectory Directory(string? wrapperError = null)
    {
        var directory = new FakeMcpToolDirectory(wrapperError);
        directory.Add("calendar-mcp", "get_events", wrapped: true, fingerprint: "fp-events", schema: EventsSchema);
        directory.Add("calendar-mcp", "list_accounts", wrapped: true, fingerprint: "fp-accounts");
        directory.Add("calendar-mcp", "a_tool_with_a_very_long_name_that_has_no_wrapper", wrapped: false, fingerprint: "fp-long");
        directory.Add("ms365", "search_emails", wrapped: true, fingerprint: "fp-search");
        return directory;
    }

    private static WispExecutor NewExecutor(
        IToolRegistry registry,
        IMcpToolDirectory? directory,
        TimeSpan? readinessWait = null,
        IMcpPreflightRecovery? preflight = null) =>
        new(registry, new FakeWorkingMemory(), agentLoopRunner: null!,
            new WispOptions { McpReadinessWait = readinessWait ?? TimeSpan.FromMilliseconds(50) },
            NullLogger<WispExecutor>.Instance,
            preflightRecovery: preflight,
            mcpToolDirectory: directory);

    private static void RegisterInvokeTool(FakeToolRegistry registry, out List<ToolInvokeRequest> calls)
    {
        var captured = new List<ToolInvokeRequest>();
        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "MCP", Source = "mcp" },
            new CapturingToolExecutor(captured.Add, "ok"));
        calls = captured;
    }

    private static WispDefinition Wisp(params WispStep[] steps) => new() { Description = "test", Steps = steps };

    private static WispStep McpStep(string? server, string tool, string paramsJson = "{}") => new()
    {
        Id = "s1",
        Mode = StepMode.Direct,
        Gateway = GatewayType.Mcp,
        Server = server,
        Tool = tool,
        Params = JsonDocument.Parse(paramsJson).RootElement
    };

    private static WispStep LlmStep() => new() { Id = "think", Mode = StepMode.Llm, Prompt = "Summarise." };

    private sealed class FillingPreflight(string schema, IReadOnlyDictionary<string, object?> filled) : IMcpPreflightRecovery
    {
        public Task<PreflightRecoveryResult> TryRecoverAsync(
            string serverName, string toolName, IReadOnlyList<string> missingFields,
            IReadOnlyDictionary<string, object?> existingArgs, string? parentSessionId, CancellationToken ct) =>
            Task.FromResult(new PreflightRecoveryResult(
                filled.Where(kv => missingFields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                [], null));

        public Task<string?> TryGetParametersSchemaAsync(string serverName, string toolName, CancellationToken ct) =>
            Task.FromResult<string?>(schema);
    }
}

/// <summary>An <see cref="IMcpToolDirectory"/> over a fixed tool list; records wrapper calls.</summary>
internal sealed class FakeMcpToolDirectory(string? wrapperError = null) : IMcpToolDirectory
{
    private readonly List<McpToolEntry> _entries = [];

    public List<ToolInvokeRequest> WrapperCalls { get; } = [];

    /// <summary>Wrapper lookups by typed name that miss before they start hitting.</summary>
    public int HideWrappersFor { get; set; }

    public IToolExecutor WrapperExecutor => new Executor(this);

    public void Add(string server, string tool, bool wrapped, string? fingerprint, string? schema = null)
    {
        var wrapper = wrapped
            ? new ToolRegistration
            {
                Name = $"{server}__{tool}",
                Description = $"[{server}] {tool}",
                ParametersSchema = schema,
                Source = $"mcp:{server}",
                DownstreamName = tool
            }
            : null;
        _entries.Add(new McpToolEntry(server, tool, wrapper, fingerprint));
    }

    public void SetFingerprint(string server, string tool, string? fingerprint)
    {
        var i = _entries.FindIndex(e => e.ServerName == server && e.ToolName == tool);
        _entries[i] = _entries[i] with { Fingerprint = fingerprint };
    }

    public void Remove(string server, string tool) =>
        _entries.RemoveAll(e => e.ServerName == server && e.ToolName == tool);

    public McpToolEntry? Resolve(string? serverName, string tool)
    {
        if (serverName is null)
        {
            if (HideWrappersFor > 0)
            {
                HideWrappersFor--;
                return null;
            }
            return _entries.FirstOrDefault(e => e.Wrapper?.Name == tool);
        }

        return _entries.FirstOrDefault(e =>
            string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ToolName, tool, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<McpToolEntry> ForServer(string serverName) =>
        _entries.Where(e => e.Wrapper is not null
                            && string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool IsServerIndexed(string serverName) =>
        _entries.Any(e => string.Equals(e.ServerName, serverName, StringComparison.OrdinalIgnoreCase));

    private sealed class Executor(FakeMcpToolDirectory owner) : IToolExecutor
    {
        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
        {
            owner.WrapperCalls.Add(request);
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = owner.WrapperError ?? """{"events":[]}""",
                IsError = owner.WrapperError is not null
            });
        }
    }

    public string? WrapperError => wrapperError;
}
