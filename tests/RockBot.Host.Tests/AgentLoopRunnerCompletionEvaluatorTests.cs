using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// End-to-end coverage of the completion evaluator gate in <see cref="AgentLoopRunner.RunAsync"/>
/// (#666), driven by a scripted LLM on the native path: which loops reach the evaluator, what it
/// reads, how an INCOMPLETE verdict re-prompts, and the originating-request handoff to subagents.
/// </summary>
[TestClass]
public class AgentLoopRunnerCompletionEvaluatorTests
{
    private const string IncompleteVerdict = "{\"complete\": false, \"reason\": \"promised a web search but did not run one\"}";
    private const string CompleteVerdict = "{\"complete\": true, \"reason\": \"answered\"}";

    [TestMethod]
    public async Task PromiseWithNoToolCalls_RunsEvaluator_AndReprompts()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: [CompletionEvalTriggersTests.WebSearchReply, "Per the spec (section 4.2), the limit is 64 KiB."],
            evaluatorReplies: [IncompleteVerdict]);
        var runner = CreateRunner(llm);

        var result = await runner.RunAsync(
            Conversation(CompletionEvalTriggersTests.WebSearchUser), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual("Per the spec (section 4.2), the limit is 64 KiB.", result);
        Assert.AreEqual(1, llm.EvaluatorRequests.Count, "the promise must reach the evaluator");
        StringAssert.Contains(llm.EvaluatorRequests[0], CompletionEvalTriggersTests.WebSearchUser);
        StringAssert.Contains(llm.EvaluatorRequests[0], "the agent made no tool calls this turn");

        // The re-prompt names the failure mode, not just "continue".
        var nudge = llm.LoopRequests[1].Last(m => m.Role == ChatRole.User).Text!;
        StringAssert.Contains(nudge, "promised a web search but did not run one");
        StringAssert.Contains(nudge, "Do that work now with your tools");
    }

    [TestMethod]
    public async Task PlainQuestionAnswer_SkipsEvaluator()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: ["The capital of Australia is Canberra, not Sydney."],
            evaluatorReplies: [IncompleteVerdict]);
        var runner = CreateRunner(llm);

        var result = await runner.RunAsync(
            Conversation("What's the capital of Australia?"), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual("The capital of Australia is Canberra, not Sydney.", result);
        Assert.AreEqual(0, llm.EvaluatorRequests.Count);
    }

    [TestMethod]
    public async Task Thanks_SkipsEvaluator()
    {
        var llm = new ScriptedLlmClient(loopReplies: ["You're welcome!"], evaluatorReplies: [IncompleteVerdict]);
        var runner = CreateRunner(llm);

        await runner.RunAsync(
            Conversation("thanks"), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(0, llm.EvaluatorRequests.Count);
    }

    [TestMethod]
    public async Task SideEffectingToolCall_RunsEvaluator_WithToolListAndRecentUserMessages()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: ["The deck is trimmed and tighter in pacing — 30 slides, demo moved earlier."],
            evaluatorReplies: [CompleteVerdict])
        {
            // Stands in for the FICC, which records each call it invokes into the run's ledger.
            OnLoopCall = () =>
            {
                LoopToolCallLedgerContext.Value!.Record("file_read", "path=deck.md", succeeded: true);
                LoopToolCallLedgerContext.Value!.Record("file_write", "path=deck.md, content=…", succeeded: true);
            },
        };
        var runner = CreateRunner(llm);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a test agent."),
            new(ChatRole.User, "Both talks are 60-minute slots, 13:15–14:15."),
            new(ChatRole.Assistant, "Noted — I'll plan for 60 minutes."),
            new(ChatRole.User, CompletionEvalTriggersTests.SlidePacingUser),
            new(ChatRole.Assistant, CompletionEvalTriggersTests.SlidePacingReply),
            new(ChatRole.User, CompletionEvalTriggersTests.UpdateDocUser),
        };

        await runner.RunAsync(messages, new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(1, llm.EvaluatorRequests.Count);
        var evalInput = llm.EvaluatorRequests[0];
        StringAssert.Contains(evalInput, "Both talks are 60-minute slots");
        StringAssert.Contains(evalInput, CompletionEvalTriggersTests.SlidePacingUser);
        StringAssert.Contains(evalInput, CompletionEvalTriggersTests.UpdateDocUser);
        StringAssert.Contains(evalInput, "- file_read (ok)");
        StringAssert.Contains(evalInput, "- file_write (ok, changes state)");
        Assert.AreEqual(ModelTier.Low, llm.EvaluatorTiers[0], "the evaluator keeps its cheap tier");
    }

    [TestMethod]
    public async Task SpawningLoop_IsNotEvaluated_AndRecordsOriginatingRequest()
    {
        string? requestSeenByTool = null;
        var llm = new ScriptedLlmClient(
            loopReplies: ["Done — I've handed the deck rebuild to a subagent."],
            evaluatorReplies: [IncompleteVerdict])
        {
            OnLoopCall = () =>
            {
                // What spawn_subagent reads when the model calls it mid-loop.
                requestSeenByTool = OriginatingUserRequestContext.Value;
                LoopToolCallLedgerContext.Value!.Record("spawn_subagent", "description=rebuild deck", succeeded: true);
            },
        };
        var runner = CreateRunner(llm);

        await runner.RunAsync(
            Conversation(CompletionEvalTriggersTests.UpdateDocUser), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(0, llm.EvaluatorRequests.Count, "the spawning loop's results come later — skip it");
        Assert.AreEqual(CompletionEvalTriggersTests.UpdateDocUser, requestSeenByTool);
        Assert.IsNull(OriginatingUserRequestContext.Value, "the ambient request must not leak past the run");
    }

    [TestMethod]
    public async Task SubagentSynthesis_RunsEvaluator_AgainstOriginalRequest()
    {
        const string original = "trim the deck to about 11 slides — it's a 60-minute slot";
        const string synthetic = "[Subagent task abc123 completed]: Deck built: 30 slides, validated.";
        string? requestSeenByTool = null;

        var llm = new ScriptedLlmClient(
            loopReplies: ["The deck is finished: 30 slides, validated.", "The deck is now 11 slides."],
            evaluatorReplies: ["{\"complete\": false, \"reason\": \"user asked for about 11 slides; reply says 30\"}"])
        {
            OnLoopCall = () => requestSeenByTool ??= OriginatingUserRequestContext.Value,
        };
        var runner = CreateRunner(llm);

        var result = await runner.RunAsync(
            Conversation(synthetic), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None,
            subagentSynthesis: true, originatingUserRequest: original);

        Assert.AreEqual("The deck is now 11 slides.", result);
        Assert.AreEqual(1, llm.EvaluatorRequests.Count);
        StringAssert.Contains(llm.EvaluatorRequests[0], "## Original user request");
        StringAssert.Contains(llm.EvaluatorRequests[0], original);
        StringAssert.Contains(llm.EvaluatorRequests[0], synthetic);

        // A follow-on spawn from the synthesis turn still points at the user's request.
        Assert.AreEqual(original, requestSeenByTool);

        var nudge = llm.LoopRequests[1].Last(m => m.Role == ChatRole.User).Text!;
        StringAssert.Contains(nudge, "what the user originally asked for");
    }

    [TestMethod]
    public async Task FinalReprompt_IsNotEvaluatedAgain()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: ["Done.", "Done."],
            evaluatorReplies: [IncompleteVerdict, IncompleteVerdict]);
        var runner = CreateRunner(llm);

        var result = await runner.RunAsync(
            Conversation("so do it now"), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual("Done.", result);
        Assert.AreEqual(1, llm.EvaluatorRequests.Count, "reprompt budget is unchanged: one evaluation, one re-prompt");
        Assert.AreEqual(2, llm.LoopRequests.Count);
    }

    [TestMethod]
    public async Task UnparseableVerdict_FailsOpen()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: [CompletionEvalTriggersTests.UpdatedReply],
            evaluatorReplies: ["I think so?"]);
        var runner = CreateRunner(llm);

        var result = await runner.RunAsync(
            Conversation(CompletionEvalTriggersTests.UpdateDocUser), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(CompletionEvalTriggersTests.UpdatedReply, result);
        Assert.AreEqual(1, llm.EvaluatorRequests.Count);
        Assert.AreEqual(1, llm.LoopRequests.Count);
    }

    // ── #683: subagent evidence, the user's actual request, neutral re-prompts ──

    private static List<ChatMessage> SynthesisConversation() =>
        Conversation("[Subagent task abc123 completed]: " + CompletionEvalTriggersTests.DeckRevisionReport);

    private static ScriptedLlmClient SynthesisLlm(string[] loopReplies, string[] evaluatorReplies) =>
        new(loopReplies, evaluatorReplies)
        {
            // The synthesis turn itself only reads the subagent's saved output.
            OnLoopCall = () =>
                LoopToolCallLedgerContext.Value!.Record("get_from_working_memory", "key=subagent/abc123/deck-summary", succeeded: true),
        };

    [TestMethod]
    public async Task SubagentSynthesis_SubagentWroteAndUploaded_EvaluatorSeesItsCalls_AndCompletes()
    {
        var llm = SynthesisLlm([CompletionEvalTriggersTests.DeckRevisionReport], [CompleteVerdict]);
        var runner = CreateRunner(llm);
        var relayed = new RelayedSubagentWork("abc123",
            CompletionEvalTriggers.SummarizeForRelay(CompletionEvalTriggersTests.DeckSubagentCalls()), 6);

        var result = await runner.RunAsync(
            SynthesisConversation(), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None,
            subagentSynthesis: true,
            originatingUserRequest: CompletionEvalTriggersTests.DeckRevisionRequest,
            relayedWork: [relayed]);

        Assert.AreEqual(CompletionEvalTriggersTests.DeckRevisionReport, result);
        Assert.AreEqual(1, llm.EvaluatorRequests.Count);
        Assert.AreEqual(1, llm.LoopRequests.Count, "COMPLETE: no re-prompt");

        var evalInput = llm.EvaluatorRequests[0];
        var section = evalInput[evalInput.IndexOf("## Tool calls made by subagent abc123 (the relayed work)", StringComparison.Ordinal)..];
        StringAssert.Contains(section, "- file_write (ok, changes state)");
        StringAssert.Contains(section, "- mcp_invoke_tool → upload_file (ok, changes state)");
        StringAssert.Contains(evalInput, "- get_from_working_memory (ok)");
        StringAssert.Contains(evalInput, "UserAskedFor: instruction");
    }

    [TestMethod]
    public async Task SubagentSynthesis_ClaimedUploadNeverMade_IsVisible_AndThe_RepromptIsInternal()
    {
        var llm = SynthesisLlm(
            [CompletionEvalTriggersTests.DeckRevisionReport, "The deck and notes are revised; the upload to OneDrive did not happen."],
            ["{\"complete\": false, \"reason\": \"the report claims an OneDrive upload, but the subagent's calls show none\"}"]);
        var runner = CreateRunner(llm);
        var relayed = new RelayedSubagentWork("abc123",
            CompletionEvalTriggers.SummarizeForRelay(CompletionEvalTriggersTests.DeckSubagentCalls(uploaded: false)), 4);

        await runner.RunAsync(
            SynthesisConversation(), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None,
            subagentSynthesis: true,
            originatingUserRequest: CompletionEvalTriggersTests.DeckRevisionRequest,
            relayedWork: [relayed]);

        var evalInput = llm.EvaluatorRequests[0];
        var section = evalInput[evalInput.IndexOf("## Tool calls made by subagent abc123", StringComparison.Ordinal)..];
        Assert.IsFalse(section[..section.IndexOf("## Why this reply", StringComparison.Ordinal)]
            .Contains("upload", StringComparison.OrdinalIgnoreCase), "no upload in the subagent's calls");

        Assert.AreEqual(2, llm.LoopRequests.Count, "INCOMPLETE re-prompts once");
        var nudge = llm.LoopRequests[1].Last(m => m.Role == ChatRole.User).Text!;
        Assert.IsTrue(nudge.StartsWith("[Internal completion check — not a message from the user]", StringComparison.Ordinal), nudge);
        StringAssert.Contains(nudge, "the subagent's calls show none");
        StringAssert.Contains(nudge, "do not say \"you're right\" or \"you were right\"");
        StringAssert.Contains(nudge, "Do not address or thank the user for feedback");
        StringAssert.Contains(nudge, CompletionEvalTriggersTests.DeckRevisionRequest);
        Assert.IsFalse(nudge.Contains("[Subagent task abc123", StringComparison.Ordinal),
            "the subagent's report is not quoted as the user's message");
        StringAssert.Contains(nudge, "what the user originally asked for");
        StringAssert.Contains(nudge, "do not disown it");
    }

    [TestMethod]
    public async Task SubagentSynthesis_InformationOnlyRequest_IsJudgedOnAccuracyOnly()
    {
        var llm = SynthesisLlm(
            ["Noted — since the talk doesn't exist yet, I've captured the outline the subagent drafted.", "Corrected."],
            ["{\"complete\": false, \"reason\": \"the outline's date is wrong\"}"]);
        var runner = CreateRunner(llm);

        await runner.RunAsync(
            SynthesisConversation(), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None,
            subagentSynthesis: true,
            originatingUserRequest: CompletionEvalTriggersTests.ContextOnlyUser,
            relayedWork: [new RelayedSubagentWork("abc123", [], 0)]);

        var evalInput = llm.EvaluatorRequests[0];
        StringAssert.Contains(evalInput, "UserAskedFor: information-only");
        StringAssert.Contains(evalInput, "Do NOT mark it INCOMPLETE because the user's wider goal implies more work");

        var nudge = llm.LoopRequests[1].Last(m => m.Role == ChatRole.User).Text!;
        StringAssert.Contains(nudge, "do not start work the user did not ask for");
        Assert.IsFalse(nudge.Contains("Continue working on the original request", StringComparison.Ordinal));
        Assert.IsFalse(nudge.Contains("do the remaining work", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ContextOnlyUserMessage_WithSideEffect_GetsInformationOnlyRubric()
    {
        var llm = new ScriptedLlmClient(
            loopReplies: ["Got it — I've noted that the talk is still to be written."],
            evaluatorReplies: [CompleteVerdict])
        {
            OnLoopCall = () => LoopToolCallLedgerContext.Value!.Record("file_write", "path=notes/talk.md", succeeded: true),
        };
        var runner = CreateRunner(llm);

        await runner.RunAsync(
            Conversation(CompletionEvalTriggersTests.ContextOnlyUser), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(1, llm.EvaluatorRequests.Count);
        StringAssert.Contains(llm.EvaluatorRequests[0], "UserAskedFor: information-only");
    }

    [TestMethod]
    public async Task Incomplete_CounterIsTaggedByTrigger_AndUserAskedFor()
    {
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, HostDiagnostics.CompletionCheckIncomplete))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            lock (measurements) measurements.Add((value, copy));
        });
        listener.Start();

        var llm = new ScriptedLlmClient(
            loopReplies: [CompletionEvalTriggersTests.WebSearchReply, "Per the spec (section 4.2), the limit is 64 KiB."],
            evaluatorReplies: [IncompleteVerdict]);
        var runner = CreateRunner(llm);

        await runner.RunAsync(
            Conversation(CompletionEvalTriggersTests.WebSearchUser), new ChatOptions(), "s1",
            enableFollowUp: false, cancellationToken: CancellationToken.None);

        lock (measurements)
        {
            Assert.IsTrue(measurements.Any(m =>
                    m.Value == 1
                    && Equals(m.Tags.GetValueOrDefault("rockbot.completion_check.trigger"), "promise-no-action")
                    && Equals(m.Tags.GetValueOrDefault("rockbot.completion_check.user_asked_for"), "instruction")),
                string.Join("; ", measurements.Select(m => string.Join(",", m.Tags.Select(t => $"{t.Key}={t.Value}")))));
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static List<ChatMessage> Conversation(string userMessage) =>
    [
        new(ChatRole.System, "You are a test agent."),
        new(ChatRole.User, userMessage),
    ];

    private static AgentLoopRunner CreateRunner(ILlmClient llm)
    {
        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-eval-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);

        return new AgentLoopRunner(
            llm,
            new StubWorkingMemory(),
            ModelBehavior.Default,
            new StubFeedbackStore(),
            clock,
            Options.Create(new AgentHostOptions()),
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);
    }

    /// <summary>
    /// Answers loop requests (any tier but Low, or Low with tools) from <c>loopReplies</c> and
    /// evaluator requests (Low, system prompt starts with the evaluator's) from <c>evaluatorReplies</c>.
    /// </summary>
    private sealed class ScriptedLlmClient(string[] loopReplies, string[] evaluatorReplies) : ILlmClient
    {
        private int _loop;
        private int _eval;

        public List<List<ChatMessage>> LoopRequests { get; } = [];
        public List<string> EvaluatorRequests { get; } = [];
        public List<ModelTier> EvaluatorTiers { get; } = [];
        public Action? OnLoopCall { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken cancellationToken)
        {
            var list = messages.ToList();
            var isEvaluator = list.Count > 0
                && list[0].Role == ChatRole.System
                && list[0].Text?.StartsWith("You are a task-completion evaluator", StringComparison.Ordinal) == true;

            string reply;
            if (isEvaluator)
            {
                EvaluatorRequests.Add(list[^1].Text ?? string.Empty);
                EvaluatorTiers.Add(tier);
                reply = evaluatorReplies[Math.Min(_eval++, evaluatorReplies.Length - 1)];
            }
            else
            {
                LoopRequests.Add(list);
                OnLoopCall?.Invoke();
                reply = loopReplies[Math.Min(_loop++, loopReplies.Length - 1)];
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }
    }

    private sealed class StubFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class StubSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }
}
