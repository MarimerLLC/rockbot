using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentManagerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a SubagentManager with the given concurrency limit.
    /// The scope factory provides a SubagentRunner backed by a
    /// no-op (immediately-completing) LLM client so background tasks
    /// complete cleanly without needing real infrastructure.
    /// </summary>
    /// <summary>Registers all stubs needed by SubagentRunner into the service collection.</summary>
    private static void AddSubagentRunnerStubs(IServiceCollection services, ILlmClient llmClient)
    {
        services.AddSingleton(llmClient);
        services.AddSingleton<ILlmTierSelector>(new FixedTierSelector(ModelTier.Balanced));
        services.AddSingleton<IWorkingMemory>(new NoopWorkingMemory());
        services.AddSingleton<IFeedbackStore>(new NoopFeedbackStore());
        services.AddSingleton<IToolRegistry>(new EmptyToolRegistry());
        services.AddSingleton<IMessagePublisher>(new NoopPublisher());
        services.AddSingleton<ILongTermMemory>(new NoopLongTermMemory());
        services.AddSingleton<ISkillStore>(new NoopSkillStore());
        var agentProfileOptions = Options.Create(new AgentProfileOptions());
        services.AddSingleton(agentProfileOptions);
        services.AddSingleton(new AgentClock(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>()));
        services.AddSingleton(new AgentIdentity("test-agent"));
        services.AddSingleton(ModelBehavior.Default);
        services.AddSingleton(Options.Create(new AgentHostOptions()));
        services.AddSingleton<MemoryTools>();
        services.AddSingleton(new ToolGuideTools([], NullLoggerFactory.Instance.CreateLogger<ToolGuideTools>()));
        services.AddTransient<AgentLoopRunner>();
        services.AddTransient<SubagentRunner>();

        // SubagentRunner now requires TieredChatClientRegistry to capture the
        // configured model ID into routing telemetry. A stub registry backed by a
        // no-op IChatClient is sufficient — GetModelId returns null when metadata
        // is unavailable, which is the back-compat path for tests.
        var stubChatClient = new NoopChatClient();
        services.AddSingleton(new TieredChatClientRegistry(stubChatClient, stubChatClient, stubChatClient));

        // AgentProfile is required by SubagentRunner; provide a minimal stub.
        var stubDoc = new AgentProfileDocument("stub", null, [], "");
        var stubProfile = new AgentProfile(stubDoc, stubDoc);
        services.AddSingleton(stubProfile);

        // ProfileHolder + AgentContextBuilder dependencies
        var profileHolder = new ProfileHolder();
        profileHolder.Update(stubProfile);
        services.AddSingleton(profileHolder);
        var nameHolder = new AgentNameHolder();
        services.AddSingleton(nameHolder);
        services.AddSingleton<ISystemPromptBuilder>(new DefaultSystemPromptBuilder(profileHolder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions())));
        services.AddSingleton<IRulesStore>(new NoopRulesStore());
        services.AddSingleton<IConversationMemory>(new NoopConversationMemory());
                services.AddSingleton<InjectedMemoryTracker>();
        services.AddSingleton<SkillIndexTracker>();
        services.AddSingleton<SkillRecallTracker>();
        services.AddSingleton<SessionClientCapabilityStore>();
        services.AddTransient<AgentContextBuilder>();

        // TierRoutingLogger requires a writable directory; point it at a temp folder
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        services.AddSingleton(new TierRoutingLogger(
            Options.Create(new AgentProfileOptions { BasePath = tmpDir }),
            NullLoggerFactory.Instance.CreateLogger<TierRoutingLogger>()));
    }

    private static SubagentManager CreateManager(int maxConcurrent = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());

        var provider = services.BuildServiceProvider();

        var opts = Options.Create(new SubagentOptions { MaxConcurrentSubagents = maxConcurrent });
        return new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            opts,
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance);
    }

    /// <summary>
    /// Creates a SubagentManager whose SubagentRunner blocks until the
    /// <paramref name="blockUntil"/> TCS is signalled or the token is cancelled.
    /// Useful for testing in-flight task tracking and cancellation.
    /// </summary>
    private static (SubagentManager manager, TaskCompletionSource<bool> release) CreateBlockingManager()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new BlockingLlmClient(tcs));

        var provider = services.BuildServiceProvider();

        var opts = Options.Create(new SubagentOptions { MaxConcurrentSubagents = 3 });
        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            opts,
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance);

        return (manager, tcs);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SpawnAsync_ReturnsValidTaskId()
    {
        var manager = CreateManager();

        var result = await manager.SpawnAsync(
            "Test task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Should be a 12-char lowercase hex string, not an error
        Assert.IsFalse(result.StartsWith("Error:"),
            $"Expected task ID but got error: {result}");
        Assert.AreEqual(12, result.Length,
            $"Expected 12-char task ID but got '{result}'");
        Assert.IsTrue(result.All(c => "0123456789abcdef".Contains(c)),
            $"Task ID contains non-hex chars: {result}");
    }

    [TestMethod]
    public async Task SpawnAsync_WhenAtLimit_ReturnsErrorMessage()
    {
        // MaxConcurrentSubagents = 0 means any spawn is immediately rejected
        var manager = CreateManager(maxConcurrent: 0);

        var result = await manager.SpawnAsync(
            "Test task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        StringAssert.StartsWith(result, "Error:",
            $"Expected error message but got: {result}");
        StringAssert.Contains(result, "0");
    }

    [TestMethod]
    public async Task CancelAsync_UnknownId_ReturnsFalse()
    {
        var manager = CreateManager();

        var cancelled = await manager.CancelAsync("nonexistent-id");

        Assert.IsFalse(cancelled);
    }

    [TestMethod]
    public async Task CancelAsync_StopsRunningTask_ReturnsTrue()
    {
        var (manager, release) = CreateBlockingManager();

        // Spawn a task that blocks until we signal or cancel
        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session-1", ct: CancellationToken.None);

        Assert.IsFalse(taskId.StartsWith("Error:"), "Expected valid task ID");

        // Give the background task a moment to start (reach the LLM call)
        await Task.Delay(100);

        // Cancel should find and stop the task
        var cancelled = await manager.CancelAsync(taskId);

        Assert.IsTrue(cancelled, "CancelAsync should return true for an active task");

        // After cancel, the task should not be listed as active
        var active = manager.ListActive();
        Assert.IsFalse(active.Any(e => e.TaskId == taskId),
            "Cancelled task should not appear in ListActive");
    }

    [TestMethod]
    public async Task ListActive_ReturnsRunningTasks()
    {
        var (manager, release) = CreateBlockingManager();

        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Give the background task a moment to start
        await Task.Delay(100);

        var active = manager.ListActive();

        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(taskId, active[0].TaskId);
        Assert.AreEqual("Blocking task", active[0].Description);
        Assert.AreEqual("session-1", active[0].PrimarySessionId);

        // Clean up
        release.SetResult(true);
        await manager.CancelAsync(taskId);
    }

    [TestMethod]
    public async Task ListActive_AfterCompletion_ReturnsEmpty()
    {
        var manager = CreateManager(); // uses NoopLlmClient — completes immediately

        await manager.SpawnAsync(
            "Quick task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Wait briefly for the background task to complete
        await Task.Delay(200);

        var active = manager.ListActive();
        Assert.AreEqual(0, active.Count);
    }

    // ── Originating user request (#666) ─────────────────────────────────────────

    [TestMethod]
    public async Task SpawnAsync_RecordsOriginatingUserRequestOnTheEntry()
    {
        var (manager, release) = CreateBlockingManager();

        var taskId = await manager.SpawnAsync(
            "Rebuild the deck at ~30 slides", context: null, timeoutMinutes: 10,
            primarySessionId: "session/blazor-session", ct: CancellationToken.None,
            originatingUserRequest: "figure out a way to update the doc");
        await Task.Delay(100);

        var entry = manager.ListActive().Single(e => e.TaskId == taskId);
        Assert.AreEqual("figure out a way to update the doc", entry.OriginatingUserRequest);

        release.SetResult(true);
        await manager.CancelAsync(taskId);
    }

    [TestMethod]
    public async Task SpawnAsync_PublishedResultCarriesOriginatingUserRequest()
    {
        var publisher = new CapturingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());
        services.AddSingleton<IMessagePublisher>(publisher); // last registration wins
        var provider = services.BuildServiceProvider();

        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions { MaxConcurrentSubagents = 3 }),
            publisher,
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance);

        await manager.SpawnAsync(
            "Rebuild the deck", context: null, timeoutMinutes: null,
            primarySessionId: "session/blazor-session", ct: CancellationToken.None,
            originatingUserRequest: "trim the deck to about 11 slides");

        var result = await publisher.WaitForResultAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(result, "the runner should publish a result");
        Assert.AreEqual("trim the deck to about 11 slides", result.OriginatingUserRequest);
    }

    // ── Consequential-action scope (#685) ───────────────────────────────────────

    [TestMethod]
    public async Task SpawnAsync_CapturesTheSpawningRunsActionScope_AsASubagents()
    {
        var (manager, release) = CreateBlockingManager();
        string taskId;
        using (ActionGateContext.Set(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly),
                   new ConsequentialActionGate(Options.Create(new AgentHostOptions()))))
        {
            taskId = await manager.SpawnAsync(
                "Place prep blocks for the talk", context: null, timeoutMinutes: 10,
                primarySessionId: "session/cli", ct: CancellationToken.None,
                originatingUserRequest: "the talk doesn't exist yet");
        }
        await Task.Delay(100);

        var entry = manager.ListActive().Single(e => e.TaskId == taskId);
        Assert.AreEqual(new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly), entry.ActionGate);

        release.SetResult(true);
        await manager.CancelAsync(taskId);
    }

    [TestMethod]
    public async Task SpawnAsync_PublishedResultCarriesTheActionScope()
    {
        var publisher = new CapturingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());
        services.AddSingleton<IMessagePublisher>(publisher); // last registration wins
        var provider = services.BuildServiceProvider();

        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions { MaxConcurrentSubagents = 3 }),
            publisher,
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance);

        using (ActionGateContext.Set(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly),
                   new ConsequentialActionGate(Options.Create(new AgentHostOptions()))))
        {
            await manager.SpawnAsync(
                "Place prep blocks", context: null, timeoutMinutes: null,
                primarySessionId: "session/cli", ct: CancellationToken.None);
        }

        var result = await publisher.WaitForResultAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(result, "the runner should publish a result");
        Assert.AreEqual("subagent-of-user-turn", result.RunOrigin);
        Assert.AreEqual("information-only", result.UserAskedFor);
        Assert.AreEqual(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly),
            ActionGateScope.FromRelayedResults([result]), "the synthesis turn runs under the same gate");
    }

    [TestMethod]
    public void CapOriginatingRequest_BlankIsNull_LongIsCapped()
    {
        Assert.IsNull(SubagentManager.CapOriginatingRequest("   "));
        Assert.IsNull(SubagentManager.CapOriginatingRequest(null));
        Assert.AreEqual(SubagentManager.MaxOriginatingRequestChars,
            SubagentManager.CapOriginatingRequest(new string('x', 10_000))!.Length);
    }

    // ── Subagent lineage (#665) ─────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the 2026-10-10 replay of session cli-deck-01612: turn m2 spawned a research subagent
    /// whose grounded summary (stateless core, server/discover, MRTR …) was saved to working memory;
    /// two turns later the deck subagent's context did not include it and the deck mentioned none of
    /// it. The deck subagent must now start with the research inlined and its key listed — without
    /// the primary passing anything.
    /// </summary>
    [TestMethod]
    public async Task DeckSubagent_AfterResearchSubagent_StartsWithTheResearch()
    {
        const string ResearchKey = "subagent/6a393b675e2f/mcp-2026-07-28-primary-source-summary";
        const string Research =
            "MCP 2026-07-28 primary-source summary: stateless per-request versioning via _meta / " +
            "MCP-Protocol-Version; server/discover replaces the initialize handshake; capability and " +
            "extension negotiation; MRTR InputRequiredResult / inputResponses; Streamable HTTP; Origin validation.";

        var memory = new DictionaryWorkingMemory();
        memory.Values[ResearchKey] = Research;
        memory.Values["subagent/6a393b675e2f/web-https___modelcontextprotocol.io_spec-chunk0"] = "raw page";

        var registry = new SessionWorkRegistry();
        // The research result as SubagentResultHandler records it when it arrives (turn m2).
        registry.RecordSubagentResult("session/cli-deck-01612", SubagentWorkResult.Create(
            "6a393b675e2f",
            "Research the key features of the MCP version 2 spec (2026-07-28) from primary sources",
            "Grounded summary saved. Key changes: stateless core, server/discover, MRTR. " +
            $"Saved to {ResearchKey}.",
            [ResearchKey, "subagent/6a393b675e2f/web-https___modelcontextprotocol.io_spec-chunk0"],
            isSuccess: true, DateTimeOffset.UtcNow.AddMinutes(-6)));
        // An earlier, unrequested draft the primary wrote (the file the bad deck was based on).
        registry.RecordToolCall("cli-deck-01612", "file_write",
            [new("path", "drafts/techorama-nl-mcp-v2-production-deck.md"), new("content", "old draft")], true);

        var llm = new CapturingLlmClient();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, llm);
        services.AddSingleton<IWorkingMemory>(memory);
        services.AddSingleton<ISessionWorkRegistry>(registry);
        var provider = services.BuildServiceProvider();

        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions()),
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance,
            registry);

        // Turn m4: "create a Slidev deck" — no context, no inputs, no key passed by the primary.
        var taskId = await manager.SpawnAsync(
            "Create a Slidev deck for the Techorama talk on MCP v2", context: null, timeoutMinutes: null,
            primarySessionId: "session/cli-deck-01612", ct: CancellationToken.None,
            originatingUserRequest: "create a Slidev deck");
        Assert.IsFalse(taskId.StartsWith("Error:"), taskId);

        var messages = await llm.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var context = string.Join("\n", messages.Select(m => m.Text));

        StringAssert.Contains(context, "Prior work in this conversation");
        StringAssert.Contains(context, "server/discover", "the research must be inlined into the deck subagent's context");
        StringAssert.Contains(context, ResearchKey, "the research key must be listed");
        StringAssert.Contains(context, "drafts/techorama-nl-mcp-v2-production-deck.md", "files written so far are listed");
        StringAssert.Contains(context, "the research wins");
        StringAssert.Contains(context, "create a Slidev deck", "the originating user request is shown");
        Assert.IsFalse(context.Contains("raw page"), "bulk web chunks are counted, not inlined");
    }

    [TestMethod]
    public async Task SpawnAsync_LinksTheSubagentSessionToThePrimary()
    {
        var registry = new SessionWorkRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());
        var provider = services.BuildServiceProvider();
        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions()),
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance,
            registry);

        var taskId = await manager.SpawnAsync("Quick task", null, null, "session/s1", CancellationToken.None);

        Assert.AreEqual("s1", registry.ResolveRootSession($"subagent-{taskId}"));
        Assert.AreEqual("s1", registry.ResolveRootSession($"subagent/{taskId}"));
    }

    [TestMethod]
    public async Task SpawnAsync_ResearchTaskWithLowCap_RunsWithTheFloor()
    {
        var publisher = new CapturingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());
        services.AddSingleton<IMessagePublisher>(publisher);
        var provider = services.BuildServiceProvider();
        var capturing = new ListLogger<SubagentManager>();
        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions()),
            publisher,
            new AgentIdentity("TestBot"),
            capturing);

        await manager.SpawnAsync("Research the MCP v2 spec and outline a talk", null, null,
            "session/s1", CancellationToken.None, maxIterations: 8);

        Assert.IsTrue(capturing.Messages.Any(m => m.Contains("raised max_iterations from 8 to 20")),
            string.Join("\n", capturing.Messages));
        Assert.IsNotNull(await publisher.WaitForResultAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// #683: the subagent's own tool calls travel on its result, so the primary's synthesis check
    /// sees the write and upload the report describes.
    /// </summary>
    [TestMethod]
    public async Task SubagentResult_CarriesTheRunsToolCalls()
    {
        var publisher = new CapturingPublisher();
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new LedgerRecordingLlmClient());
        services.AddSingleton<IMessagePublisher>(publisher);
        var provider = services.BuildServiceProvider();
        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubagentOptions()),
            publisher,
            new AgentIdentity("TestBot"),
            NullLogger<SubagentManager>.Instance);

        await manager.SpawnAsync("Revise the deck and upload it", null, null, "session/s1", CancellationToken.None);
        var result = await publisher.WaitForResultAsync(TimeSpan.FromSeconds(10));

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.ToolCallCount);
        Assert.IsNotNull(result.ToolCalls);
        Assert.AreEqual(new SubagentToolCallSummary("file_write", true, true, "path=deck.md"), result.ToolCalls[0]);
        Assert.AreEqual("mcp_invoke_tool → upload_file", result.ToolCalls[1].Name);
        Assert.IsTrue(result.ToolCalls[1].ChangesState);
    }

    /// <summary>Stands in for the FICC: records a write and an upload into the run's ledger, then answers.</summary>
    private sealed class LedgerRecordingLlmClient : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (LoopToolCallLedgerContext.Value is { } ledger && ledger.Snapshot().Count == 0)
            {
                ledger.Record("file_write", "path=deck.md", succeeded: true);
                ledger.Record("mcp_invoke_tool", "server_name=onedrive, tool_name=upload_file, path=/Talks/deck.md", succeeded: true);
            }
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Deck revised and uploaded.")]));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    /// <summary>LLM client that records the messages of its first call, then answers immediately.</summary>
    private sealed class CapturingLlmClient : ILlmClient
    {
        public TaskCompletionSource<IReadOnlyList<ChatMessage>> FirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            FirstCall.TrySetResult(messages.ToList());
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Deck written.")]));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class DictionaryWorkingMemory : IWorkingMemory
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Values { get; } = new();

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null,
            IReadOnlyList<string>? tags = null)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(Values
                .Where(kv => prefix is null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new WorkingMemoryEntry(kv.Key, kv.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)))
                .ToList());

        public Task DeleteAsync(string key)
        {
            Values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            ListAsync(prefix);
    }

    private sealed class CapturingPublisher : IMessagePublisher
    {
        private readonly TaskCompletionSource<SubagentResultMessage> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (topic.StartsWith(SubagentTopics.Result, StringComparison.Ordinal)
                && envelope.GetPayload<SubagentResultMessage>() is { } result)
                _result.TrySetResult(result);
            return Task.CompletedTask;
        }

        public async Task<SubagentResultMessage?> WaitForResultAsync(TimeSpan timeout)
        {
            var done = await Task.WhenAny(_result.Task, Task.Delay(timeout));
            return done == _result.Task ? _result.Task.Result : null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── ISubagentSessionResolver ────────────────────────────────────────────────

    [TestMethod]
    public void Resolver_RecognizesSubagentSessionForms()
    {
        var manager = CreateManager();

        Assert.IsTrue(manager.IsSubagentSession("subagent/abc123"));
        Assert.IsTrue(manager.IsSubagentSession("session/subagent-abc123"));
        Assert.IsTrue(manager.IsSubagentSession("subagent-abc123"));
        Assert.IsFalse(manager.IsSubagentSession("session/blazor-session"));
        Assert.IsFalse(manager.IsSubagentSession("wisp-xyz"));
        Assert.IsFalse(manager.IsSubagentSession(""));
    }

    [TestMethod]
    public async Task Resolver_ActiveSubagent_IsActiveAndResolvesPrimary()
    {
        var (manager, release) = CreateBlockingManager();
        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session/blazor-session", ct: CancellationToken.None);
        await Task.Delay(100);

        var sessionId = $"subagent/{taskId}";
        Assert.IsTrue(manager.IsActive(sessionId));
        Assert.AreEqual("session/blazor-session", manager.ResolvePrimarySession(sessionId));

        release.SetResult(true);
        await manager.CancelAsync(taskId);
    }

    [TestMethod]
    public async Task Resolver_TerminatedSubagent_ResolvesPrimaryFromTombstone()
    {
        var (manager, release) = CreateBlockingManager();
        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session/blazor-session", ct: CancellationToken.None);
        await Task.Delay(100);

        await manager.CancelAsync(taskId); // removes from active, records tombstone

        var sessionId = $"subagent/{taskId}";
        Assert.IsFalse(manager.IsActive(sessionId));
        // Tombstone still resolves the owning primary so a late A2A reply can fold back.
        Assert.AreEqual("session/blazor-session", manager.ResolvePrimarySession(sessionId));
    }

    [TestMethod]
    public void Resolver_UnknownSubagent_ResolvesNull()
    {
        var manager = CreateManager();
        Assert.IsNull(manager.ResolvePrimarySession("subagent/never-existed"));
        Assert.IsFalse(manager.IsActive("subagent/never-existed"));
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    /// <summary>LLM client that immediately returns an empty response.</summary>
    private sealed class NoopLlmClient : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Return a minimal valid response with no tool calls
            var msg = new ChatMessage(ChatRole.Assistant, "Task complete.");
            return Task.FromResult(new ChatResponse([msg]));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ModelTier tier,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    /// <summary>
    /// Minimal IChatClient used to construct a TieredChatClientRegistry for SubagentRunner.
    /// Never receives an actual call — exists only so DI can build the registry.
    /// </summary>
    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "")]));

#pragma warning disable CS1998 // async without await is intentional for the yield-break empty stream
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>LLM client that blocks until the TCS is completed or the token is cancelled.</summary>
    private sealed class BlockingLlmClient(TaskCompletionSource<bool> tcs) : ILlmClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Block until either signal or cancellation
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task;
            cancellationToken.ThrowIfCancellationRequested();
            var msg = new ChatMessage(ChatRole.Assistant, "Done.");
            return new ChatResponse([msg]);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ModelTier tier,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class NoopWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value,
            TimeSpan? ttl = null, string? category = null,
            IReadOnlyList<string>? tags = null) => Task.CompletedTask;

        public Task<string?> GetAsync(string key) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class NoopFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);

        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolRegistration> GetTools() => [];
        public IToolExecutor? GetExecutor(string toolName) => null;
        public void Register(ToolRegistration registration, IToolExecutor executor) { }
        public bool Unregister(string toolName) => false;
    }

    private sealed class NoopLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults,
            CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class NoopPublisher : IMessagePublisher
    {
        public Task PublishAsync(string topic, MessageEnvelope envelope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }

    private sealed class NoopConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);

        public Task ClearAsync(string sessionId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
