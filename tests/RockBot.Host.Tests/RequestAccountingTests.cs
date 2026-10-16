using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host.Tests;

/// <summary>
/// #670: per-request context accounting — <see cref="RequestAccountingChatClient"/>, the per-run
/// <see cref="RequestAccountingContext"/> collector, and <see cref="ContextCategorizer"/>.
/// </summary>
[TestClass]
public class RequestAccountingTests
{
    private sealed class UsageChatClient(long input, long cached, long output) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = input,
                    CachedInputTokenCount = cached,
                    OutputTokenCount = output,
                },
                ModelId = "test-model",
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static ChatOptions OptionsWithTools(params string[] names) => new()
    {
        Tools = names.Select(n => (AITool)AIFunctionFactory.Create((string x) => x, n, "Echoes " + n)).ToList(),
    };

    [TestMethod]
    public async Task Request_IsRecordedIntoTheActiveRun_WithProviderUsageAndToolSchemaSize()
    {
        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var sut = new RequestAccountingChatClient(new UsageChatClient(30_000, 20_000, 500), logger, "Balanced");
        var options = OptionsWithTools("save_memory", "web_search");
        var diagnostics = new LoopDiagnostics();

        using (RequestAccountingContext.Begin("s1", logger, diagnostics))
        {
            await sut.GetResponseAsync([new ChatMessage(ChatRole.System, "persona"), new ChatMessage(ChatRole.User, "hi")], options);
            var record = RequestAccountingContext.Value!.Snapshot().Single();

            Assert.AreEqual(30_000, record.InputTokens);
            Assert.AreEqual(20_000, record.CachedInputTokens);
            Assert.AreEqual(500, record.OutputTokens);
            Assert.AreEqual(2, record.ToolCount);
            Assert.AreEqual("persona".Length + "hi".Length, record.MessageChars);
            Assert.AreEqual(options.Tools!.Sum(RequestAccountingChatClient.MeasureSchema), record.ToolSchemaChars);
            Assert.IsTrue(record.ToolSchemaChars > "save_memory".Length + "web_search".Length,
                "schema size includes descriptions and parameter schemas");
        }

        Assert.AreEqual(1, diagnostics.LlmRequests);
        Assert.AreEqual(30_000, diagnostics.MaxRequestInputTokens);

        var line = logger.Entries.Single(e => e.Text.StartsWith("LLM request:", StringComparison.Ordinal)).Text;
        StringAssert.Contains(line, "session=s1");
        StringAssert.Contains(line, "input=30000");
        StringAssert.Contains(line, "tools=2");
        StringAssert.Contains(line, "system-prompt:7");
        StringAssert.Contains(line, "user-message:2");
    }

    [TestMethod]
    public async Task Run_LogsSummary_WithMedianOverLoopRequestsOnly()
    {
        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var tools = OptionsWithTools("file_read");

        using (RequestAccountingContext.Begin("s1", logger))
        {
            foreach (var input in new long[] { 10_000, 40_000, 20_000 })
            {
                var sut = new RequestAccountingChatClient(new UsageChatClient(input, 0, 10), logger, "Low");
                await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], tools);
            }

            // An evaluator-style call with no tools is auxiliary: counted in the sums, not the median.
            var aux = new RequestAccountingChatClient(new UsageChatClient(1_000, 0, 10), logger, "Low");
            await aux.GetResponseAsync([new ChatMessage(ChatRole.User, "judge")]);
        }

        var summary = logger.Entries.Single(e => e.Text.StartsWith("Turn context summary:", StringComparison.Ordinal)).Text;
        StringAssert.Contains(summary, "requests=3");
        StringAssert.Contains(summary, "aux=1");
        StringAssert.Contains(summary, "inputMedian=20000");
        StringAssert.Contains(summary, "inputMax=40000");
        StringAssert.Contains(summary, "inputSum=71000");
        Assert.IsNull(RequestAccountingContext.Value, "the scope restores the previous (empty) collector");
    }

    [TestMethod]
    public async Task NestedRun_KeepsItsOwnRequests()
    {
        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var tools = OptionsWithTools("file_read");
        var sut = new RequestAccountingChatClient(new UsageChatClient(5_000, 0, 1), logger, "Low");

        using (RequestAccountingContext.Begin("parent", logger))
        {
            await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "a")], tools);
            using (RequestAccountingContext.Begin("child", logger))
            {
                await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "b")], tools);
                await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "c")], tools);
                Assert.AreEqual(2, RequestAccountingContext.Value!.Snapshot().Count);
            }
            Assert.AreEqual(1, RequestAccountingContext.Value!.Snapshot().Count);
        }
    }

    [TestMethod]
    public async Task Request_OutsideAnyRun_IsLoggedButNotCollected()
    {
        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var sut = new RequestAccountingChatClient(new UsageChatClient(5_000, 0, 1), logger, "Low");

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "a")]);

        Assert.AreEqual(1, logger.Entries.Count(e => e.Text.StartsWith("LLM request:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Median_HandlesEvenOddAndEmpty()
    {
        Assert.AreEqual(0, RequestAccountingContext.Median([]));
        Assert.AreEqual(5, RequestAccountingContext.Median([5]));
        Assert.AreEqual(15, RequestAccountingContext.Median([10, 20]));
        Assert.AreEqual(20, RequestAccountingContext.Median([10, 20, 90]));
    }

    [TestMethod]
    public void Categorizer_UsesTagsThenRoleAndPosition()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "file_read")]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "body")]);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "persona"),
            new ChatMessage(ChatRole.System, "Recalled from long-term memory").Tag(ContextCategory.LongTermMemory),
            new ChatMessage(ChatRole.User, "earlier question").Tag(ContextCategory.HistoryUser),
            new ChatMessage(ChatRole.Assistant, "earlier answer").Tag(ContextCategory.HistoryAssistant),
            new(ChatRole.User, ContextCategorizer.SubagentRelayPrefix + "abc completed]: long output"),
            new(ChatRole.System, "untagged block"),
            new ChatMessage(ChatRole.User, "current question").Tag(ContextCategory.HistoryUser),
            call,
            result,
            new(ChatRole.Assistant, "thinking aloud"),
        ];

        var categories = ContextCategorizer.Classify(messages);

        CollectionAssert.AreEqual(
            new[]
            {
                ContextCategory.SystemPrompt,
                ContextCategory.LongTermMemory,
                ContextCategory.HistoryUser,
                ContextCategory.HistoryAssistant,
                ContextCategory.SubagentRelay,
                ContextCategory.SystemOther,
                ContextCategory.UserMessage,
                ContextCategory.ToolCalls,
                ContextCategory.ToolResults,
                ContextCategory.LoopAssistant,
            },
            categories);
    }

    [TestMethod]
    public void Categorizer_SkillRecallFramingWrap_IsRecognisedWhenTagged()
    {
        var skill = new Skill("decks/slidev", "summary", "Use v-click.", DateTimeOffset.UtcNow);
        var msg = new ChatMessage(ChatRole.System, SkillRecallFraming.Wrap(skill)).Tag(ContextCategory.SkillBody);

        Assert.AreEqual(ContextCategory.SkillBody, ContextCategorizer.Classify([new(ChatRole.System, "p"), msg])[1]);
    }

    // ── Tool-list changes (#712) ─────────────────────────────────────────────

    private static readonly List<ChatMessage> Turn = [new(ChatRole.System, "persona"), new(ChatRole.User, "hi")];

    private static RequestAccountingContext.State Begin(out IDisposable scope, LoopDiagnostics? diagnostics)
    {
        scope = RequestAccountingContext.Begin("s", new CapturingLogger<RequestAccountingChatClient>(), diagnostics);
        return RequestAccountingContext.Value!;
    }

    private static async Task<(List<string> Changes, CapturingLogger<RequestAccountingChatClient> Logger)> RunRequests(
        Func<RequestAccountingChatClient, LoopDiagnostics, Task> requests)
    {
        var changes = new List<string>();
        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var sut = new RequestAccountingChatClient(new UsageChatClient(1_000, 0, 10), logger, "Balanced");
        var diagnostics = new LoopDiagnostics();

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, HostDiagnostics.LlmRequestToolListChanges))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rockbot.tool_list.change")
                    lock (changes) changes.Add((string)tag.Value!);
            }
        });
        listener.Start();

        using (RequestAccountingContext.Begin("tool-list-test", logger, diagnostics))
            await requests(sut, diagnostics);

        return (changes, logger);
    }

    private static bool WarnedOfCacheMiss(CapturingLogger<RequestAccountingChatClient> logger) =>
        logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Text.Contains("misses the prompt cache"));

    [TestMethod]
    public void ObserveTools_SameList_IsNoChange()
    {
        var state = Begin(out var scope, new LoopDiagnostics());
        using (scope)
        {
            Assert.IsNull(state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools));
            Assert.IsNull(state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools));
        }
    }

    [TestMethod]
    public void ObserveTools_GrowthWithoutAToolCall_IsUnrequested()
    {
        var state = Begin(out var scope, new LoopDiagnostics());
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools);
            var change = state.ObserveTools("Balanced", OptionsWithTools("a", "b", "cal__get_events").Tools);

            Assert.IsNotNull(change);
            Assert.IsFalse(change.Requested);
            CollectionAssert.AreEqual(new[] { "cal__get_events" }, change.Added.ToArray());
            Assert.AreEqual(0, change.Removed.Count);
            Assert.AreEqual(2, change.PreviousCount);
            Assert.AreEqual(3, change.Count);
        }
    }

    [TestMethod]
    public void ObserveTools_GrowthAfterAToolCall_IsRequested()
    {
        var diagnostics = new LoopDiagnostics();
        var state = Begin(out var scope, diagnostics);
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a", "mcp_find_tools").Tools);
            diagnostics.ToolCalls++; // the model called the loader
            var change = state.ObserveTools("Balanced", OptionsWithTools("a", "mcp_find_tools", "cal__get_events").Tools);

            Assert.IsTrue(change!.Requested);
        }
    }

    [TestMethod]
    public void ObserveTools_Reorder_IsAChange()
    {
        var state = Begin(out var scope, new LoopDiagnostics());
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools);
            var change = state.ObserveTools("Balanced", OptionsWithTools("b", "a").Tools);

            Assert.IsFalse(change!.Requested);
            Assert.AreEqual(0, change.Added.Count + change.Removed.Count);
        }
    }

    [TestMethod]
    public void ObserveTools_ToollessRequest_NeitherComparesNorReplacesTheBaseline()
    {
        var state = Begin(out var scope, new LoopDiagnostics());
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools);
            Assert.IsNull(state.ObserveTools("Balanced", null), "the completion evaluator sends no tools");
            Assert.IsNull(state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools));
        }
    }

    [TestMethod]
    public void ObserveTools_TierSwitch_StartsAFreshBaseline()
    {
        var state = Begin(out var scope, new LoopDiagnostics());
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools);
            Assert.IsNull(state.ObserveTools("High", OptionsWithTools("a").Tools),
                "another model has its own cache; its first request has nothing to compare with");
        }
    }

    [TestMethod]
    public void ObserveTools_WithoutDiagnostics_IsNotClassified()
    {
        var state = Begin(out var scope, diagnostics: null);
        using (scope)
        {
            state.ObserveTools("Balanced", OptionsWithTools("a").Tools);
            Assert.IsNull(state.ObserveTools("Balanced", OptionsWithTools("a", "b").Tools));
        }
    }

    [TestMethod]
    public async Task Client_UnrequestedChange_CountsAndWarns()
    {
        var (changes, logger) = await RunRequests(async (sut, _) =>
        {
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "b"));
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "b", "cal__get_events"));
        });

        CollectionAssert.AreEqual(new[] { "unrequested" }, changes);
        Assert.IsTrue(WarnedOfCacheMiss(logger));
        Assert.IsTrue(logger.Entries.Any(e => e.Text.Contains("added=[cal__get_events]")));
    }

    [TestMethod]
    public async Task Client_RequestedChange_CountsWithoutWarning()
    {
        var (changes, logger) = await RunRequests(async (sut, diagnostics) =>
        {
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "mcp_find_tools"));
            diagnostics.ToolCalls++;
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "mcp_find_tools", "cal__get_events"));
        });

        CollectionAssert.AreEqual(new[] { "requested" }, changes);
        Assert.IsFalse(WarnedOfCacheMiss(logger));
    }

    [TestMethod]
    public async Task TextBasedLoop_StructuredLoaderCall_NextRequestIsRequested()
    {
        // The manual loop also dispatches structured FunctionCallContent. That branch must count the
        // call, or a loader's activation reads as unrequested on the next request (#712 review).
        var changes = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, HostDiagnostics.LlmRequestToolListChanges))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rockbot.tool_list.change")
                    lock (changes) changes.Add((string)tag.Value!);
            }
        });
        listener.Start();

        var options = new ChatOptions();
        var activated = AIFunctionFactory.Create(() => "events", "cal__get_events");
        options.Tools =
        [
            AIFunctionFactory.Create((string? query = null) =>
            {
                options.Tools!.Add(activated); // what a loader's activation does
                return "cal__get_events is now callable";
            }, "mcp_find_tools"),
        ];

        var logger = new CapturingLogger<RequestAccountingChatClient>();
        var model = new RequestAccountingChatClient(
            new ConsequentialActionGateTests.ToolThenTextChatClient(
                new FunctionCallContent("call-1", "mcp_find_tools", new Dictionary<string, object?> { ["query"] = "calendar" }),
                "Reply."),
            logger, "Balanced");
        var diagnostics = new LoopDiagnostics();
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased: true, new AgentHostOptions());

        await runner.RunAsync(ConsequentialActionGateTests.Conversation("What's on my calendar?"), options, "s1",
            enableFollowUp: false, enableCompletionEval: false, diagnostics: diagnostics,
            cancellationToken: CancellationToken.None);

        Assert.AreEqual(1, diagnostics.ToolCalls);
        Assert.AreEqual("mcp_find_tools", diagnostics.LastToolName);
        CollectionAssert.AreEqual(new[] { "requested" }, changes);
        Assert.IsFalse(WarnedOfCacheMiss(logger));
    }

    [TestMethod]
    public async Task Client_UnchangedList_RecordsNothing()
    {
        var (changes, logger) = await RunRequests(async (sut, _) =>
        {
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "b"));
            await sut.GetResponseAsync(Turn, OptionsWithTools("a", "b"));
        });

        Assert.AreEqual(0, changes.Count);
        Assert.IsFalse(WarnedOfCacheMiss(logger));
    }
}
