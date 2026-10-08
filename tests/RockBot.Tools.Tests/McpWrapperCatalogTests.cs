using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpWrapperNamingTests
{
    [TestMethod]
    [DataRow("adjutant", "send_email", "adjutant__send_email")]
    [DataRow("onedrive-marimer", "list_files", "onedrive-marimer__list_files")]
    [DataRow("microsoft.learn", "docs.search", "microsoft-learn__docs-search")]
    [DataRow("todo", "add task", "todo__add-task")]
    public void Name_IsSanitisedToTheProvidersCharset(string server, string tool, string expected) =>
        Assert.AreEqual(expected, McpWrapperNaming.For(server, tool));

    [TestMethod]
    public void LongNames_AreNotTruncated_ButFailTheLimit()
    {
        var name = McpWrapperNaming.For("server", new string('t', 70));

        Assert.IsTrue(name.Length > McpWrapperNaming.MaxLength);
        Assert.IsFalse(McpWrapperNaming.FitsProviderLimit(name));
    }

    [TestMethod]
    public void TryParse_SplitsOnTheFirstSeparator()
    {
        Assert.IsTrue(McpWrapperNaming.TryParse("agg__adjutant__send_email", out var server, out var tool));
        Assert.AreEqual("agg", server);
        Assert.AreEqual("adjutant__send_email", tool);
        Assert.IsFalse(McpWrapperNaming.TryParse("plain", out _, out _));
    }

    [TestMethod]
    public void PromptName_IsTheTypedNameWithASuffix() =>
        Assert.AreEqual("microsoft-learn__daily_briefing-prompt", McpWrapperNaming.ForPrompt("microsoft.learn", "daily_briefing"));
}

/// <summary>
/// The typed-tool registry follows the bridge's index: tools appear, change and disappear with
/// their servers, an unchanged index doesn't churn registrations, and the collision rules hold.
/// </summary>
[TestClass]
public class McpWrapperCatalogTests
{
    private const string SendEmailSchema = """{"type":"object","properties":{"to":{"type":"string"},"subject":{"type":"string"}},"required":["to","subject"]}""";

    private readonly ToolRegistry _registry = new();
    private readonly Dictionary<string, IReadOnlyList<McpToolDefinition>?> _serverTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<McpPromptDefinition>> _serverPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrackingPublisher _publisher = new();
    private ToolSchemaCache _cache = null!;

    private static McpToolDefinition Tool(string name, string? schema = null, string description = "Does a thing.") =>
        new() { Name = name, Description = description, ParametersSchema = schema };

    private static McpServerSummary Summary(string server, string id = "abc123def456") =>
        new() { ServerName = server, ServerId = id, Fingerprint = Guid.NewGuid().ToString("N") };

    private McpWrapperCatalog Catalog(McpWrapperMode mode = McpWrapperMode.Eager)
    {
        _cache = ToolSchemaCache.WithPrompts((server, _) => Task.FromResult(_serverTools.GetValueOrDefault(server) is { } tools
            ? new McpServerSurface(tools, _serverPrompts.GetValueOrDefault(server) ?? [])
            : null));
        var identity = new AgentIdentity("test-agent");
        var proxy = new McpToolProxy(_publisher, new StubSubscriber(), identity, NullLogger<McpToolProxy>.Instance);
        var management = new McpManagementExecutor(
            new McpServerIndex(), proxy, _publisher, new StubSubscriber(), identity,
            NullLogger<McpManagementExecutor>.Instance);

        return new McpWrapperCatalog(
            _registry, _cache, management,
            Options.Create(new McpToolSurfaceOptions { WrapperMode = mode }),
            NullLogger<McpWrapperCatalog>.Instance);
    }

    private static McpServersIndexed Indexed(params McpServerSummary[] servers) => new() { Servers = [.. servers] };

    private ToolRegistration? Registered(string name) => _registry.GetTools().FirstOrDefault(t => t.Name == name);

    private static McpPromptDefinition Prompt(string name, params string[] required) => new()
    {
        Name = name,
        Description = "A workflow.",
        Arguments = [.. required.Select(a => new McpPromptArgument { Name = a, Required = true })]
    };

    // ── Typed prompt tools (#616) ─────────────────────────────────────────────

    [TestMethod]
    public async Task ToolAndPromptOfTheSameName_GetDistinctNames_AndBothResolve()
    {
        _serverTools["adjutant"] = [Tool("daily_briefing")];
        _serverPrompts["adjutant"] = [Prompt("daily_briefing", "date")];
        var catalog = Catalog(McpWrapperMode.Pinned);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.IsTrue(catalog.TryGet("adjutant__daily_briefing", out var tool));
        Assert.AreEqual("daily_briefing", tool.ToolName);
        Assert.IsTrue(catalog.TryGetPrompt("adjutant__daily_briefing-prompt", out var prompt));
        Assert.AreEqual("daily_briefing", prompt.PromptName);
        Assert.AreEqual("""{"type":"object","properties":{"date":{"type":"string"}},"required":["date"]}""", prompt.InputSchema);
        Assert.IsFalse(catalog.TryGet("adjutant__daily_briefing-prompt", out _), "A prompt is never looked up as a tool.");
        Assert.IsFalse(catalog.TryGetPrompt("adjutant__daily_briefing", out _));
    }

    [TestMethod]
    public async Task PromptNameOverTheLimit_IsSkipped()
    {
        _serverTools["adjutant"] = [Tool("send_email")];
        _serverPrompts["adjutant"] = [Prompt(new string('p', 60)), Prompt("triage")];
        var catalog = Catalog(McpWrapperMode.Lazy);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "adjutant__triage-prompt" }, catalog.PromptWrappers.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task PromptNamedLikeATypedTool_GivesWayToTheTool()
    {
        // The tool "x-prompt" and the prompt "x" both map to adjutant__x-prompt.
        _serverTools["adjutant"] = [Tool("x-prompt")];
        _serverPrompts["adjutant"] = [Prompt("x")];
        var catalog = Catalog(McpWrapperMode.Lazy);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.IsTrue(catalog.TryGet("adjutant__x-prompt", out _));
        Assert.AreEqual(0, catalog.PromptWrappers.Count);
    }

    [TestMethod]
    [DataRow(McpWrapperMode.Lazy, DisplayName = "Lazy")]
    [DataRow(McpWrapperMode.Pinned, DisplayName = "Pinned")]
    public async Task PromptTools_StayOutOfTheToolListsAndTheRegistry(McpWrapperMode mode)
    {
        _serverTools["adjutant"] = [Tool("send_email")];
        _serverPrompts["adjutant"] = [Prompt("daily_briefing", "date")];
        var catalog = Catalog(mode);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreEqual(1, catalog.PromptWrappers.Count);
        CollectionAssert.AreEqual(new[] { "adjutant__send_email" }, catalog.Wrappers.Select(w => w.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "adjutant__send_email" }, catalog.WrappersFor("adjutant").Select(w => w.Name).ToArray());
        Assert.AreEqual(0, _registry.GetTools().Count);
    }

    [TestMethod]
    public async Task EagerMode_HasNoPromptTools()
    {
        _serverTools["adjutant"] = [Tool("send_email")];
        _serverPrompts["adjutant"] = [Prompt("daily_briefing", "date")];
        var catalog = Catalog(McpWrapperMode.Eager);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreEqual(0, catalog.PromptWrappers.Count);
        Assert.IsFalse(_registry.GetTools().Any(t => t.Name.EndsWith("-prompt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OffMode_RegistersNothing()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema)];
        var catalog = Catalog(McpWrapperMode.Off);

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreEqual(0, _registry.GetTools().Count);
    }

    [TestMethod]
    public async Task Eager_RegistersEachToolWithItsSchemaUnchanged()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema, "Sends an email."), Tool("list_accounts")];
        var catalog = Catalog();

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        var send = Registered("adjutant__send_email");
        Assert.IsNotNull(send);
        Assert.AreEqual(SendEmailSchema, send.ParametersSchema);
        Assert.AreEqual("[adjutant] Sends an email.", send.Description);
        Assert.AreEqual("mcp:adjutant", send.Source);
        Assert.AreEqual("send_email", send.DownstreamName);
        Assert.IsNotNull(Registered("adjutant__list_accounts"));
        Assert.IsTrue(catalog.TryGet("adjutant__send_email", out var wrapper));
        Assert.AreEqual("abc123def456", wrapper.ServerId);
    }

    [TestMethod]
    public async Task SameToolNameOnTwoServers_GetsTwoWrappers()
    {
        _serverTools["onedrive-marimer"] = [Tool("list_files")];
        _serverTools["onedrive-personal"] = [Tool("list_files")];
        var catalog = Catalog();

        await catalog.ApplyAsync(Indexed(Summary("onedrive-marimer"), Summary("onedrive-personal", "fedcba987654")), CancellationToken.None);

        Assert.AreEqual("list_files", Registered("onedrive-marimer__list_files")!.DownstreamName);
        Assert.AreEqual("mcp:onedrive-personal", Registered("onedrive-personal__list_files")!.Source);
    }

    [TestMethod]
    public async Task UnchangedIndex_KeepsTheSameRegistrations()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema)];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);
        var before = Registered("adjutant__send_email");

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreSame(before, Registered("adjutant__send_email"), "An unchanged tool must not be re-registered.");
    }

    [TestMethod]
    public async Task ChangedSchema_ReplacesTheRegistration()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema)];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        var widened = SendEmailSchema.Replace("\"required\":[\"to\",\"subject\"]", "\"required\":[\"to\"]");
        _serverTools["adjutant"] = [Tool("send_email", widened)];
        _cache.Invalidate("adjutant");
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreEqual(widened, Registered("adjutant__send_email")!.ParametersSchema);
    }

    [TestMethod]
    public async Task RemovedToolsAndServers_LoseTheirWrappers()
    {
        _serverTools["adjutant"] = [Tool("send_email"), Tool("list_accounts")];
        _serverTools["todo"] = [Tool("add_task")];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant"), Summary("todo")), CancellationToken.None);

        _serverTools["adjutant"] = [Tool("list_accounts")];
        _cache.Invalidate("adjutant");
        await catalog.ApplyAsync(new McpServersIndexed { Servers = [Summary("adjutant")], RemovedServers = ["todo"] }, CancellationToken.None);

        Assert.IsNull(Registered("adjutant__send_email"));
        Assert.IsNotNull(Registered("adjutant__list_accounts"));
        Assert.IsNull(Registered("todo__add_task"));
        Assert.IsFalse(catalog.TryGet("todo__add_task", out _));
    }

    [TestMethod]
    public async Task WrapperNeverDisplacesANativeTool()
    {
        _registry.Register(new ToolRegistration { Name = "web__search", Description = "native", Source = "web" }, new NoopExecutor());
        _serverTools["web"] = [Tool("search")];
        var catalog = Catalog();

        await catalog.ApplyAsync(Indexed(Summary("web")), CancellationToken.None);

        Assert.AreEqual("native", Registered("web__search")!.Description);
        Assert.IsFalse(catalog.TryGet("web__search", out _));
    }

    [TestMethod]
    public async Task ToolsThatSanitiseToTheSameName_KeepTheFirst()
    {
        _serverTools["todo"] = [Tool("add.task", description: "first"), Tool("add-task", description: "second")];
        var catalog = Catalog();

        await catalog.ApplyAsync(Indexed(Summary("todo")), CancellationToken.None);

        Assert.AreEqual("[todo] first", Registered("todo__add-task")!.Description);
        Assert.AreEqual("add.task", Registered("todo__add-task")!.DownstreamName);
    }

    [TestMethod]
    public async Task NameOverTheProviderLimit_IsNotRegistered()
    {
        _serverTools["adjutant"] = [Tool(new string('x', 70)), Tool("send_email")];
        var catalog = Catalog();

        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.AreEqual(1, _registry.GetTools().Count);
        Assert.IsNotNull(Registered("adjutant__send_email"));
    }

    [TestMethod]
    public async Task UnreadableSchemas_LeaveExistingWrappersInPlace()
    {
        _serverTools["adjutant"] = [Tool("send_email")];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        _serverTools["adjutant"] = null;
        _cache.Invalidate("adjutant");
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);

        Assert.IsNotNull(Registered("adjutant__send_email"));
    }

    // ── Executor ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MissingRequiredKey_IsRefusedWithoutCallingTheDownstream()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema)];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);
        var executor = _registry.GetExecutor("adjutant__send_email")!;

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "1",
            ToolName = "adjutant__send_email",
            Arguments = """{"to":"a@b.c"}"""
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "required parameter(s) [subject] are missing");
        Assert.IsFalse(_publisher.Published.Any(p => p.Topic == McpToolProxy.InvokeTopic),
            "The downstream must not be called.");
    }

    [TestMethod]
    public async Task CompleteCall_GoesToTheDownstreamToolByItsOwnName()
    {
        _serverTools["adjutant"] = [Tool("send_email", SendEmailSchema)];
        var catalog = Catalog();
        await catalog.ApplyAsync(Indexed(Summary("adjutant")), CancellationToken.None);
        var executor = _registry.GetExecutor("adjutant__send_email")!;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await executor.ExecuteAsync(new ToolInvokeRequest
            {
                ToolCallId = "1",
                ToolName = "adjutant__send_email",
                Arguments = """{"to":"a@b.c","subject":"hi"}"""
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // No bridge in this test; only the published request matters.
        }

        var published = _publisher.Published.Single(p => p.Topic == McpToolProxy.InvokeTopic).Envelope;
        var request = published.GetPayload<ToolInvokeRequest>()!;
        Assert.AreEqual("send_email", request.ToolName);
        Assert.AreEqual("""{"to":"a@b.c","subject":"hi"}""", request.Arguments);
        Assert.AreEqual("adjutant", published.Headers[McpHeaders.ServerName]);
    }

    private sealed class NoopExecutor : IToolExecutor
    {
        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
            Task.FromResult(new ToolInvokeResponse { ToolCallId = request.ToolCallId, ToolName = request.ToolName });
    }
}
