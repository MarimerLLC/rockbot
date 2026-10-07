using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

/// <summary>
/// Lazy typed MCP tools (#612): <c>mcp_find_tools</c> scoring, per-session activation, eviction
/// when a server changes or goes, and how activations reach a run's tool list. Per-tier modes and
/// pinned servers (#613): what the registry holds and how each run's list is fitted to its tier.
/// </summary>
[TestClass]
public class McpLazyTypedToolsTests
{
    private const string SendEmailSchema =
        """{"type":"object","properties":{"to":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"}},"required":["to","subject"]}""";

    private static readonly Dictionary<string, (string Summary, McpToolDefinition[] Tools)> Servers = new()
    {
        ["adjutant"] = ("Email, calendar and contacts", [
            new() { Name = "send_email", Description = "Send an email message.", ParametersSchema = SendEmailSchema },
            new() { Name = "list_emails", Description = "List the messages in a mail folder." },
            new() { Name = "search_email", Description = "Search messages by keyword." },
            new() { Name = "create_event", Description = "Create a calendar event." }
        ]),
        ["chat"] = ("Team chat", [
            new() { Name = "send_message", Description = "Post a message to a channel." }
        ])
    };

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway
    {
        public required ToolRegistry Registry { get; init; }
        public required McpServerIndex Index { get; init; }
        public required McpTypedToolSurface Surface { get; init; }
        public required McpServersIndexedHandler Handler { get; init; }
        public required Dictionary<string, (string Summary, McpToolDefinition[] Tools)> Live { get; init; }

        public Task PublishAsync(params string[] servers) => DeliverAsync(new McpServersIndexed
        {
            Servers = [.. servers.Select(Summary)]
        });

        public Task DeliverAsync(McpServersIndexed message) =>
            Handler.HandleAsync(message, new MessageHandlerContext
            {
                Envelope = message.ToEnvelope("test-bridge"),
                Agent = new AgentIdentity("test-agent"),
                Services = null!,
                CancellationToken = CancellationToken.None
            });

        public McpServerSummary Summary(string server)
        {
            var (summary, tools) = Live[server];
            return new McpServerSummary
            {
                ServerName = server,
                ServerId = $"id-{server}",
                Summary = summary,
                ToolCount = tools.Length,
                ToolNames = [.. tools.Select(t => t.Name)],
                Fingerprint = McpSurfaceFingerprint.Server(tools.Select(t => (t.Name, (string?)t.Description, t.ParametersSchema)), []),
                ToolFingerprints = tools.ToDictionary(t => t.Name, t => McpSurfaceFingerprint.Tool(t.Name, t.Description, t.ParametersSchema))
            };
        }

        public async Task<JsonDocument> FindAsync(string query, string? session, int? limit = null)
        {
            var args = limit is null
                ? JsonSerializer.Serialize(new { query })
                : JsonSerializer.Serialize(new { query, limit });
            var response = await Registry.GetExecutor(McpTypedToolSurface.FindToolsName)!.ExecuteAsync(
                new ToolInvokeRequest { ToolCallId = "c1", ToolName = McpTypedToolSurface.FindToolsName, Arguments = args, SessionId = session },
                CancellationToken.None);
            Assert.IsFalse(response.IsError, response.Content);
            return JsonDocument.Parse(response.Content!);
        }

        public string[] Activated(string session, TypedToolMode mode = TypedToolMode.Lazy) =>
            [.. Surface.GetActivated(session, mode).Select(f => f.Name)];
    }

    private static async Task<Gateway> CreateAsync(
        McpWrapperMode mode = McpWrapperMode.Lazy,
        int cap = 40,
        TimeProvider? time = null,
        Action<McpToolSurfaceOptions>? configure = null)
    {
        var live = Servers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var surfaceOptions = new McpToolSurfaceOptions { WrapperMode = mode, MaxActivatedToolsPerSession = cap };
        configure?.Invoke(surfaceOptions);
        var options = Options.Create(surfaceOptions);
        var identity = new AgentIdentity("test-agent");
        var index = new McpServerIndex();
        var management = new McpManagementExecutor(index,
            new McpToolProxy(new TrackingPublisher(), new StubSubscriber(), identity, NullLogger<McpToolProxy>.Instance),
            new TrackingPublisher(), new StubSubscriber(), identity, NullLogger<McpManagementExecutor>.Instance);
        var cache = new ToolSchemaCache((server, _) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>?>(live.TryGetValue(server, out var s) ? s.Tools : null));
        var registry = new ToolRegistry();
        var surface = new McpTypedToolSurface(options, NullLogger<McpTypedToolSurface>.Instance, time);
        var catalog = new McpWrapperCatalog(registry, cache, management, options,
            NullLogger<McpWrapperCatalog>.Instance, surface);
        var handler = new McpServersIndexedHandler(registry, index, management,
            NullLogger<McpServersIndexedHandler>.Instance, cache, catalog);

        var gateway = new Gateway { Registry = registry, Index = index, Surface = surface, Handler = handler, Live = live };
        await gateway.PublishAsync([.. live.Keys]);
        return gateway;
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static McpWrapperTool Wrapper(string server, string tool, string? description = null) =>
        new(McpWrapperNaming.For(server, tool), server, null, tool, description, null, "fp");

    [TestMethod]
    public void Score_AddsTheAggregatorsWeightsPerToken()
    {
        var tool = Wrapper("adjutant", "send_email", "Sends mail.");

        // send: tool-name token 80, in description 10. email: tool-name token 80, in server text 5.
        var score = McpToolSearch.Score(tool, "send email", ["send", "email"], "adjutant  email and calendar");

        Assert.AreEqual(175, score);
    }

    [TestMethod]
    public void Score_TakesOnlyTheFirstNameRuleThatApplies()
    {
        var tool = Wrapper("mailer", "sendemail");

        Assert.AreEqual(McpToolSearch.InToolName, McpToolSearch.Score(tool, "send", ["send"], ""));
        Assert.AreEqual(McpToolSearch.InTypedName, McpToolSearch.Score(tool, "mailer", ["mailer"], ""));
        Assert.AreEqual(McpToolSearch.ToolNameToken, McpToolSearch.Score(Wrapper("x", "sendEmail"), "email", ["email"], ""),
            "camelCase tool names split into tokens like snake_case ones.");
    }

    [TestMethod]
    public void Rank_ExactNameWinsOutright_AndSingleCharacterTokensAreIgnored()
    {
        var tools = new[] { Wrapper("adjutant", "send_email", "send send send"), Wrapper("chat", "send_message") };

        var ranked = McpToolSearch.Rank("chat__send_message", tools, [], 5);
        Assert.AreEqual("chat__send_message", ranked[0].Tool.Name);
        Assert.IsTrue(ranked[0].Score >= McpToolSearch.ExactMatch);

        CollectionAssert.AreEqual(new[] { "send", "email" }, McpToolSearch.Tokens("a send-email x").ToArray());
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Lazy_RegistersFindTools_ButNoTypedTools()
    {
        var gateway = await CreateAsync();

        Assert.IsNotNull(gateway.Registry.GetExecutor(McpTypedToolSurface.FindToolsName));
        Assert.IsFalse(gateway.Registry.GetTools().Any(t => t.Name.Contains("__")),
            "Lazy mode keeps typed tools out of the global registry.");
        Assert.AreEqual("mcp:management", gateway.Registry.GetTools().Single(t => t.Name == "mcp_find_tools").Source);
    }

    [TestMethod]
    public async Task Eager_RegistersTypedTools_AndNoFindTools()
    {
        var gateway = await CreateAsync(McpWrapperMode.Eager);

        Assert.IsNull(gateway.Registry.GetExecutor(McpTypedToolSurface.FindToolsName));
        Assert.IsNotNull(gateway.Registry.GetExecutor("adjutant__send_email"));
        Assert.IsNull(gateway.Surface.ActivateByName("s", "adjutant__send_email"), "Nothing to activate in eager mode.");
    }

    // ── mcp_find_tools ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task FindTools_SendEmail_RanksSendEmailFirst_WithItsFullSchema()
    {
        var gateway = await CreateAsync();

        using var result = await gateway.FindAsync("send email", "session/a");
        var first = result.RootElement.GetProperty("tools")[0];

        Assert.AreEqual("adjutant__send_email", first.GetProperty("name").GetString());
        Assert.AreEqual("adjutant", first.GetProperty("server").GetString());
        Assert.AreEqual("id-adjutant", first.GetProperty("serverId").GetString());
        Assert.AreEqual("send_email", first.GetProperty("tool").GetString());
        Assert.IsTrue(first.GetProperty("activated").GetBoolean());
        Assert.AreEqual(2, first.GetProperty("inputSchema").GetProperty("required").GetArrayLength());
    }

    [TestMethod]
    public async Task FindTools_ActivatesForTheCallingSessionOnly()
    {
        var gateway = await CreateAsync();

        using var _ = await gateway.FindAsync("send email", "session/a", limit: 1);

        CollectionAssert.AreEqual(new[] { "adjutant__send_email" }, gateway.Activated("session/a"));
        Assert.AreEqual(0, gateway.Activated("session/b").Length, "Another session must not see the activation.");
    }

    [TestMethod]
    public async Task FindTools_WithoutASession_ReturnsMatchesButActivatesNothing()
    {
        var gateway = await CreateAsync();

        using var result = await gateway.FindAsync("send email", session: null);

        Assert.IsFalse(result.RootElement.GetProperty("tools")[0].GetProperty("activated").GetBoolean());
        StringAssert.Contains(result.RootElement.GetProperty("note").GetString(), "mcp_invoke_tool");
    }

    [TestMethod]
    public async Task FindTools_NoMatch_SaysSo()
    {
        var gateway = await CreateAsync();

        using var result = await gateway.FindAsync("quantum teleport", "session/a");

        Assert.AreEqual(0, result.RootElement.GetProperty("tools").GetArrayLength());
        StringAssert.Contains(result.RootElement.GetProperty("note").GetString(), "No tools matched");
    }

    // ── Activation ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ActivateByName_ValidTypedTool_ActivatesIt_UnknownNameDoesNot()
    {
        var gateway = await CreateAsync();

        var function = gateway.Surface.ActivateByName("session/a", "chat__send_message");

        Assert.AreEqual("chat__send_message", function?.Name);
        Assert.AreEqual("session/a", function!.GetService<ISessionBoundTool>()?.SessionId);
        CollectionAssert.AreEqual(new[] { "chat__send_message" }, gateway.Activated("session/a"));
        Assert.IsNull(gateway.Surface.ActivateByName("session/a", "chat__no_such_tool"));
    }

    [TestMethod]
    public async Task Activation_OverTheCap_DropsTheOldest_AndReactivatingKeepsThePlace()
    {
        var gateway = await CreateAsync(cap: 2);

        gateway.Surface.ActivateByName("s", "adjutant__send_email");
        gateway.Surface.ActivateByName("s", "adjutant__list_emails");
        gateway.Surface.ActivateByName("s", "adjutant__send_email");
        CollectionAssert.AreEqual(new[] { "adjutant__send_email", "adjutant__list_emails" }, gateway.Activated("s"));

        gateway.Surface.ActivateByName("s", "chat__send_message");
        CollectionAssert.AreEqual(new[] { "adjutant__list_emails", "chat__send_message" }, gateway.Activated("s"));
    }

    [TestMethod]
    public async Task IdleSession_IsDroppedByTheNextSweep()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-07T12:00:00Z"));
        var gateway = await CreateAsync(time: time);

        gateway.Surface.ActivateByName("idle", "adjutant__send_email");
        time.Now += TimeSpan.FromHours(13);
        gateway.Surface.ActivateByName("busy", "adjutant__send_email");

        Assert.AreEqual(0, gateway.Activated("idle").Length);
        Assert.AreEqual(1, gateway.Activated("busy").Length);
    }

    // ── Eviction ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ChangedTool_IsEvicted_UnchangedOnesStay()
    {
        var gateway = await CreateAsync();
        gateway.Surface.ActivateByName("s", "adjutant__send_email");
        gateway.Surface.ActivateByName("s", "adjutant__list_emails");

        var (summary, tools) = gateway.Live["adjutant"];
        gateway.Live["adjutant"] = (summary, [tools[0] with { Description = "Send an email, now with attachments." }, .. tools[1..]]);
        await gateway.PublishAsync("adjutant");

        CollectionAssert.AreEqual(new[] { "adjutant__list_emails" }, gateway.Activated("s"));

        // The next search brings it back, with the new description.
        using var result = await gateway.FindAsync("send email", "s", limit: 1);
        StringAssert.Contains(result.RootElement.GetProperty("tools")[0].GetProperty("description").GetString(), "attachments");
        CollectionAssert.AreEquivalent(new[] { "adjutant__list_emails", "adjutant__send_email" }, gateway.Activated("s"));
    }

    [TestMethod]
    public async Task RemovedServer_EvictsItsActivations()
    {
        var gateway = await CreateAsync();
        gateway.Surface.ActivateByName("s", "chat__send_message");
        gateway.Surface.ActivateByName("s", "adjutant__send_email");

        await gateway.DeliverAsync(new McpServersIndexed { Servers = [], RemovedServers = ["chat"] });

        CollectionAssert.AreEqual(new[] { "adjutant__send_email" }, gateway.Activated("s"));
        Assert.IsNull(gateway.Surface.ActivateByName("s", "chat__send_message"));
    }

    // ── Reaching the run's tool list ──────────────────────────────────────────

    private static ChatOptions OptionsFor(Gateway gateway, string session, params string[] registryTools) => new()
    {
        Tools = [.. gateway.Registry.BuildAgentToolFunctions(session, "batch",
            r => registryTools.Contains(r.Name))]
    };

    [TestMethod]
    public async Task AddActivated_AddsTheSessionsTools_OnceEach()
    {
        var gateway = await CreateAsync();
        gateway.Surface.ActivateByName("session/a", "adjutant__send_email");
        var options = OptionsFor(gateway, "session/a", "mcp_find_tools", "mcp_invoke_tool");

        using (TypedToolSurfaceContext.Set(gateway.Surface, ModelTier.Balanced))
        {
            Assert.AreEqual(1, TypedToolSurfaceContext.AddActivated(options));
            Assert.AreEqual(0, TypedToolSurfaceContext.AddActivated(options));
        }

        var added = options.Tools!.OfType<AIFunction>().Single(t => t.Name == "adjutant__send_email");
        Assert.AreEqual("session/a", added.GetService<ISessionBoundTool>()?.SessionId,
            "Added tools carry the same tool session id as the run's other registry tools.");
    }

    [TestMethod]
    public async Task AddActivated_RunWithoutTheLoaderTool_GetsNothing()
    {
        var gateway = await CreateAsync();
        gateway.Surface.ActivateByName("session/a", "adjutant__send_email");
        var options = OptionsFor(gateway, "session/a", "mcp_invoke_tool");

        using (TypedToolSurfaceContext.Set(gateway.Surface, ModelTier.Balanced))
        {
            Assert.AreEqual(0, TypedToolSurfaceContext.AddActivated(options),
                "A tool profile without mcp_find_tools must not gain typed tools.");
            Assert.IsNull(TypedToolSurfaceContext.TryActivate(options, "adjutant__send_email"));
        }
    }

    [TestMethod]
    public async Task TryActivate_ReadOnlyToolList_IsReplacedWithOneThatHasTheTool()
    {
        var gateway = await CreateAsync();
        var options = OptionsFor(gateway, "session/a", "mcp_find_tools");
        options.Tools = options.Tools!.ToArray();

        using (TypedToolSurfaceContext.Set(gateway.Surface, ModelTier.Balanced))
            Assert.IsNotNull(TypedToolSurfaceContext.TryActivate(options, "adjutant__send_email"));

        CollectionAssert.Contains(options.Tools.Select(t => t.Name).ToList(), "adjutant__send_email");
    }

    [TestMethod]
    public async Task Added_Tool_IsWrappedLikeTheLoader()
    {
        var gateway = await CreateAsync();
        gateway.Surface.ActivateByName("session/a", "adjutant__send_email");
        var options = new ChatOptions
        {
            Tools = gateway.Registry.BuildAgentToolFunctions("session/a", "batch", r => r.Name == "mcp_find_tools")
                .WithChunking(new NullWorkingMemory(), "session/a", RockBot.Llm.ModelBehavior.Default, NullLogger.Instance)
        };

        using (TypedToolSurfaceContext.Set(gateway.Surface, ModelTier.Balanced))
            TypedToolSurfaceContext.AddActivated(options);

        Assert.IsInstanceOfType<ChunkingAIFunction>(options.Tools!.Single(t => t.Name == "adjutant__send_email"));
    }

    [TestMethod]
    public async Task TypedNamesFor_PicksTheServersToolsThatFitTheQuery()
    {
        var gateway = await CreateAsync();

        var names = gateway.Surface.TypedNamesFor(gateway.Summary("adjutant"), "create a calendar event for friday", 2);

        Assert.AreEqual("adjutant__create_event", names[0]);
    }

    // ── Per-tier modes (#613) ─────────────────────────────────────────────────

    private static Task<Gateway> MixedAsync(Action<McpToolSurfaceOptions>? more = null) =>
        CreateAsync(McpWrapperMode.Off, configure: o =>
        {
            o.WrapperModeByTier[ModelTier.Low] = McpWrapperMode.Eager;
            o.WrapperModeByTier[ModelTier.High] = McpWrapperMode.Lazy;
            more?.Invoke(o);
        });

    private static string[] Names(ChatOptions options) => [.. options.Tools!.Select(t => t.Name)];

    [TestMethod]
    public void Options_TierOverride_WinsOverTheDefault()
    {
        var options = new McpToolSurfaceOptions { WrapperMode = McpWrapperMode.Lazy };
        options.WrapperModeByTier[ModelTier.Low] = McpWrapperMode.Eager;

        Assert.AreEqual(McpWrapperMode.Eager, options.ModeFor(ModelTier.Low));
        Assert.AreEqual(McpWrapperMode.Lazy, options.ModeFor(ModelTier.Balanced));
        Assert.IsTrue(options.RegistersWrappers);
        Assert.IsTrue(options.ActivatesWrappers);
        Assert.IsFalse(options.PinsServers);
        Assert.AreEqual("Low=Eager, Balanced=Lazy, High=Lazy", options.Describe());
    }

    [TestMethod]
    public void Options_BindFromConfiguration_ByTierName()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpBridge:WrapperMode"] = "Lazy",
                ["McpBridge:WrapperModeByTier:Low"] = "Eager",
                ["McpBridge:WrapperModeByTier:High"] = "Pinned"
            })
            .Build();

        var options = new McpToolSurfaceOptions();
        ConfigurationBinder.Bind(config.GetSection("McpBridge"), options);

        Assert.AreEqual("Low=Eager, Balanced=Lazy, High=Pinned", options.Describe());
    }

    [TestMethod]
    public async Task MixedTiers_RegistryHoldsTypedTools_AndFindTools()
    {
        var gateway = await MixedAsync();

        Assert.IsNotNull(gateway.Registry.GetExecutor("adjutant__send_email"), "Low is eager.");
        Assert.IsNotNull(gateway.Registry.GetExecutor(McpTypedToolSurface.FindToolsName), "High is lazy.");
    }

    [TestMethod]
    [DataRow(ModelTier.Low, true, false, DisplayName = "Low (eager): typed tools, no mcp_find_tools")]
    [DataRow(ModelTier.Balanced, false, false, DisplayName = "Balanced (off): neither")]
    [DataRow(ModelTier.High, false, true, DisplayName = "High (lazy): mcp_find_tools, no typed tools")]
    public async Task Shape_FitsTheRunsListToItsTier(ModelTier tier, bool typed, bool loader)
    {
        var gateway = await MixedAsync();
        var options = new ChatOptions { Tools = [.. gateway.Registry.BuildAgentToolFunctions("session/a", "batch")] };

        using (TypedToolSurfaceContext.Set(gateway.Surface, tier))
            TypedToolSurfaceContext.Shape(options);

        var names = Names(options);
        Assert.AreEqual(typed, names.Contains("adjutant__send_email"));
        Assert.AreEqual(typed, names.Contains("chat__send_message"));
        Assert.AreEqual(loader, names.Contains(McpTypedToolSurface.FindToolsName));
        CollectionAssert.Contains(names, "mcp_invoke_tool", "The generic path stays in every mode.");
    }

    [TestMethod]
    public async Task MixedTiers_ActivationsReachOnlyTheActivatingTier()
    {
        var gateway = await MixedAsync();
        gateway.Surface.ActivateByName("session/a", "adjutant__send_email");

        ChatOptions Run(ModelTier tier)
        {
            var options = new ChatOptions { Tools = [.. gateway.Registry.BuildAgentToolFunctions("session/a", "batch")] };
            using (TypedToolSurfaceContext.Set(gateway.Surface, tier))
            {
                TypedToolSurfaceContext.Shape(options);
                TypedToolSurfaceContext.AddActivated(options);
            }
            return options;
        }

        var high = Names(Run(ModelTier.High));
        CollectionAssert.Contains(high, "adjutant__send_email");
        CollectionAssert.DoesNotContain(high, "chat__send_message");
        CollectionAssert.DoesNotContain(Names(Run(ModelTier.Balanced)), "adjutant__send_email");
    }

    [TestMethod]
    public async Task Shape_OutsideARun_LeavesTheListAlone()
    {
        var gateway = await MixedAsync();
        var options = new ChatOptions { Tools = [.. gateway.Registry.BuildAgentToolFunctions("session/a", "batch")] };
        var before = Names(options);

        Assert.AreEqual(0, TypedToolSurfaceContext.Shape(options));
        CollectionAssert.AreEqual(before, Names(options));
    }

    // ── Pinned servers (#613) ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Pin_PinnedRunGetsTheServersTypedTools_LazyRunDoesNot()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned);

        gateway.Surface.Pin("session/a", "adjutant");

        CollectionAssert.AreEquivalent(
            new[] { "adjutant__send_email", "adjutant__list_emails", "adjutant__search_email", "adjutant__create_event" },
            gateway.Activated("session/a", TypedToolMode.Pinned));
        Assert.AreEqual(0, gateway.Activated("session/a", TypedToolMode.Lazy).Length);
        Assert.AreEqual(0, gateway.Activated("session/b", TypedToolMode.Pinned).Length, "Pins are per session.");
    }

    [TestMethod]
    public async Task Pin_ActivationsComeFirst_ThenPinnedServers_WithoutDuplicates()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned);
        gateway.Surface.ActivateByName("session/a", "chat__send_message");
        gateway.Surface.ActivateByName("session/a", "adjutant__create_event");

        gateway.Surface.Pin("session/a", "adjutant");

        var names = gateway.Activated("session/a", TypedToolMode.Pinned);
        CollectionAssert.AreEqual(new[] { "chat__send_message", "adjutant__create_event" }, names.Take(2).ToArray());
        Assert.AreEqual(names.Length, names.Distinct().Count());
        Assert.AreEqual(5, names.Length);
    }

    [TestMethod]
    public async Task Pin_UnknownServer_IsIgnored()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned);

        gateway.Surface.Pin("session/a", "made-up-server");

        Assert.AreEqual(0, gateway.Surface.GetPinned("session/a").Count);
    }

    [TestMethod]
    public async Task Pin_KeepsTheMostRecentlyCalledServers()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned, configure: o => o.MaxPinnedServersPerSession = 1);

        gateway.Surface.Pin("session/a", "adjutant");
        gateway.Surface.Pin("session/a", "chat");

        CollectionAssert.AreEqual(new[] { "chat" }, gateway.Surface.GetPinned("session/a").ToArray());
        CollectionAssert.AreEqual(new[] { "chat__send_message" }, gateway.Activated("session/a", TypedToolMode.Pinned));
    }

    [TestMethod]
    public async Task Pin_NoTierPinned_RecordsNothing()
    {
        var gateway = await CreateAsync(McpWrapperMode.Lazy);

        gateway.Surface.Pin("session/a", "adjutant");

        Assert.AreEqual(0, gateway.Surface.GetPinned("session/a").Count);
    }

    [TestMethod]
    public void Default_IsPinned()
    {
        Assert.AreEqual(McpWrapperMode.Pinned, new McpToolSurfaceOptions().WrapperMode);
        Assert.AreEqual(120, new McpToolSurfaceOptions().MaxToolsPerRequest);
    }

    [TestMethod]
    public async Task Pin_SharesTheActivationBudget_ActivationsFirst()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned, cap: 3);
        gateway.Surface.ActivateByName("session/a", "chat__send_message");

        gateway.Surface.Pin("session/a", "adjutant");

        var names = gateway.Activated("session/a", TypedToolMode.Pinned);
        Assert.AreEqual(3, names.Length, "Activations and pinned tools together stay within the cap.");
        Assert.AreEqual("chat__send_message", names[0]);
        Assert.IsTrue(names.Skip(1).All(n => n.StartsWith("adjutant__", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Pin_OverBudget_TheMostRecentlyCalledServerFillsItFirst_ListOrderStaysStable()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned, cap: 2);

        gateway.Surface.Pin("session/a", "adjutant");
        gateway.Surface.Pin("session/a", "chat");

        CollectionAssert.AreEqual(new[] { "adjutant__send_email", "chat__send_message" },
            gateway.Activated("session/a", TypedToolMode.Pinned),
            "chat (newest) gets its one tool, adjutant the rest; the older pin still lists first.");
    }

    [TestMethod]
    public async Task AddActivated_StopsAtMaxToolsPerRequest()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned, configure: o => o.MaxToolsPerRequest = 4);
        gateway.Surface.Pin("session/a", "adjutant");
        var options = OptionsFor(gateway, "session/a", "mcp_find_tools", "mcp_invoke_tool");

        using (TypedToolSurfaceContext.Set(gateway.Surface, ModelTier.Balanced))
        {
            Assert.AreEqual(2, TypedToolSurfaceContext.AddActivated(options), "2 registry tools + 2 typed = the cap of 4.");
            Assert.IsNull(TypedToolSurfaceContext.TryActivate(options, "chat__send_message"), "At the cap, nothing more joins.");
        }

        Assert.AreEqual(4, options.Tools!.Count);
    }

    [TestMethod]
    public async Task Pin_RemovedServer_DropsOutOfTheList()
    {
        var gateway = await CreateAsync(McpWrapperMode.Pinned);
        gateway.Surface.Pin("session/a", "chat");

        await gateway.DeliverAsync(new McpServersIndexed { Servers = [], RemovedServers = ["chat"] });

        Assert.AreEqual(0, gateway.Activated("session/a", TypedToolMode.Pinned).Length);
    }

    private sealed class NullWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) => Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) => Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
