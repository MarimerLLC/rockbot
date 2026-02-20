using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class SessionSummaryServiceTests
{
    private static readonly TimeSpan ShortPoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ── Skips sessions that are still active ──────────────────────────────────

    [TestMethod]
    public async Task ActiveSession_IsNotEvaluated()
    {
        var memory = new FakeConversationMemory();
        memory.AddSession("active-session", [
            new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow) // recent turn
        ]);
        var feedbackStore = new FakeFeedbackStore();
        var llm = new FakeLlmClient();

        var svc = CreateService(memory, feedbackStore, llm, idleThreshold: TimeSpan.FromMinutes(30));
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150)); // wait for a couple poll cycles
        await svc.StopAsync(CancellationToken.None);
        svc.Dispose();

        Assert.AreEqual(0, feedbackStore.AppendedEntries.Count, "Active session should not be evaluated");
    }

    // ── Evaluates sessions that have been idle long enough ────────────────────

    [TestMethod]
    public async Task IdleSession_IsEvaluated()
    {
        var memory = new FakeConversationMemory();
        memory.AddSession("idle-session", [
            new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow.AddHours(-2)) // old turn
        ]);
        var feedbackStore = new FakeFeedbackStore();
        var llm = new FakeLlmClient(responseJson: """{"summary":"session was good","overallQuality":"good"}""");

        var svc = CreateService(memory, feedbackStore, llm, idleThreshold: TimeSpan.FromMinutes(30));
        await svc.StartAsync(CancellationToken.None);

        // Wait up to 5s for the evaluation to run
        var deadline = DateTime.UtcNow.Add(WaitTimeout);
        while (feedbackStore.AppendedEntries.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        await svc.StopAsync(CancellationToken.None);
        svc.Dispose();

        Assert.IsTrue(feedbackStore.AppendedEntries.Count > 0, "Idle session should be evaluated");
        var entry = feedbackStore.AppendedEntries[0];
        Assert.AreEqual("idle-session", entry.SessionId);
        Assert.AreEqual(FeedbackSignalType.SessionSummary, entry.SignalType);
    }

    // ── Already-evaluated sessions are not re-evaluated (idempotency) ─────────

    [TestMethod]
    public async Task AlreadyEvaluatedSession_IsNotReEvaluated()
    {
        var memory = new FakeConversationMemory();
        memory.AddSession("once-session", [
            new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow.AddHours(-2))
        ]);
        var feedbackStore = new FakeFeedbackStore();
        var llm = new FakeLlmClient(responseJson: """{"summary":"ok","overallQuality":"good"}""");

        var svc = CreateService(memory, feedbackStore, llm, idleThreshold: TimeSpan.FromMinutes(30));
        await svc.StartAsync(CancellationToken.None);

        // Wait for first evaluation
        var deadline = DateTime.UtcNow.Add(WaitTimeout);
        while (feedbackStore.AppendedEntries.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Wait for several more poll cycles
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var countAfterFirstEval = feedbackStore.AppendedEntries.Count;

        await svc.StopAsync(CancellationToken.None);
        svc.Dispose();

        Assert.AreEqual(1, countAfterFirstEval, "Session should only be evaluated once");
    }

    // ── Gracefully handles LLM errors ─────────────────────────────────────────

    [TestMethod]
    public async Task LlmError_DoesNotCrashService()
    {
        var memory = new FakeConversationMemory();
        memory.AddSession("error-session", [
            new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow.AddHours(-2))
        ]);
        var feedbackStore = new FakeFeedbackStore();
        var llm = new FakeLlmClient(throwOnCall: true);

        var svc = CreateService(memory, feedbackStore, llm, idleThreshold: TimeSpan.FromMinutes(30));

        // Should not throw
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await svc.StopAsync(CancellationToken.None);
        svc.Dispose();

        // No entries appended (LLM failed), but no exception either
        Assert.AreEqual(0, feedbackStore.AppendedEntries.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SessionSummaryService CreateService(
        IConversationMemory memory,
        IFeedbackStore feedbackStore,
        ILlmClient llm,
        TimeSpan idleThreshold)
    {
        var options = Options.Create(new FeedbackOptions
        {
            PollInterval = ShortPoll,
            SessionIdleThreshold = idleThreshold,
            // Point to a nonexistent directive so the built-in fallback is used
            EvaluatorDirectivePath = "/nonexistent/session-evaluator.md"
        });
        var profileOptions = Options.Create(new AgentProfileOptions());
        return new SessionSummaryService(
            memory,
            feedbackStore,
            llm,
            options,
            profileOptions,
            NullLogger<SessionSummaryService>.Instance);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeConversationMemory : IConversationMemory
    {
        private readonly Dictionary<string, List<ConversationTurn>> _sessions = new();

        public void AddSession(string sessionId, IEnumerable<ConversationTurn> turns)
            => _sessions[sessionId] = [.. turns];

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken ct = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var list))
                _sessions[sessionId] = list = [];
            list.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken ct = default)
        {
            _sessions.TryGetValue(sessionId, out var list);
            return Task.FromResult<IReadOnlyList<ConversationTurn>>(list ?? []);
        }

        public Task ClearAsync(string sessionId, CancellationToken ct = default)
        {
            _sessions.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([.. _sessions.Keys]);
    }

    private sealed class FakeFeedbackStore : IFeedbackStore
    {
        public List<FeedbackEntry> AppendedEntries { get; } = [];

        public Task AppendAsync(FeedbackEntry entry, CancellationToken ct = default)
        {
            AppendedEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeedbackEntry>>(AppendedEntries.Where(e => e.SessionId == sessionId).ToList());

        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeedbackEntry>>(AppendedEntries.Where(e => e.Timestamp >= since).Take(maxResults).ToList());
    }

    private sealed class FakeLlmClient(string? responseJson = null, bool throwOnCall = false) : ILlmClient
    {
        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (throwOnCall)
                throw new InvalidOperationException("Simulated LLM failure");

            var content = responseJson ?? """{"summary":"session completed","overallQuality":"fair"}""";
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, content)]));
        }
    }
}
