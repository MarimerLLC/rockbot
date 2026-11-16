using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.UserProxy;
using RockBot.Agent.A2A.Tests;

namespace RockBot.Agent.Tests;

/// <summary>
/// #809: history carries each turn's id, session, origin and source, and can include the
/// broadcast sessions' output merged by time.
/// </summary>
[TestClass]
public class ConversationHistoryRequestHandlerTests
{
    private const string Thread = "blazor-session";
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ReadTurns_MapsIdSessionOriginAndSource()
    {
        var memory = new DictionaryConversationMemory();
        var origin = new ConversationTurnOrigin("blazor", "research it", T0, Thread);
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "[Subagent task abc completed]: done", T0)
        {
            Id = "t-1",
            AgentName = "subagent-abc",
            Origin = origin,
            Source = new ConversationTurnSource(ReplySourceKind.Subagent, "abc"),
        });

        var turn = (await Read(memory, includeBroadcast: false)).Single();

        Assert.AreEqual("t-1", turn.Id);
        Assert.AreEqual(Thread, turn.SessionId);
        Assert.AreEqual(new ReplyOrigin("blazor", "research it", T0, Thread), turn.Origin);
        Assert.AreEqual(ReplySourceKind.Subagent, turn.Source?.Kind);
        Assert.AreEqual("abc", turn.Source?.TaskId);
    }

    [TestMethod]
    public async Task ReadTurns_WithoutTheFlag_LeavesBroadcastOutputOut()
    {
        var memory = await WithThreadAndReportsAsync();

        var turns = await Read(memory, includeBroadcast: false);

        CollectionAssert.AreEqual(new[] { "hello", "hi" }, turns.Select(t => t.Content).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_WithTheFlag_MergesBroadcastOutputByTime()
    {
        var memory = await WithThreadAndReportsAsync();

        var turns = await Read(memory, includeBroadcast: true);

        CollectionAssert.AreEqual(
            new[] { "audit alert", "hello", "daily report", "hi", "agents reached out" },
            turns.Select(t => t.Content).ToArray());
        CollectionAssert.AreEqual(
            new[] { "scheduled-system", Thread, "scheduled", Thread, "a2a-inbound" },
            turns.Select(t => t.SessionId).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_OnATie_TheThreadsTurnsComeFirst_InTheirOwnOrder()
    {
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "first", T0));
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "report", T0));
        await memory.AddTurnAsync(Thread, new ConversationTurn("assistant", "second", T0));

        var turns = await Read(memory, includeBroadcast: true);

        CollectionAssert.AreEqual(new[] { "first", "second", "report" }, turns.Select(t => t.Content).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_ForABroadcastSession_ReturnsOnlyItsOwnTurns()
    {
        var memory = await WithThreadAndReportsAsync();

        var turns = await ConversationHistoryRequestHandler.ReadTurnsAsync(memory,
            new ConversationHistoryRequest { SessionId = WellKnownSessions.Scheduled, IncludeBroadcast = true },
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "daily report" }, turns.Select(t => t.Content).ToArray());
    }

    [TestMethod]
    public async Task HandleAsync_PublishesTheMergedHistory()
    {
        var memory = await WithThreadAndReportsAsync();
        var publisher = new TrackingPublisher();
        var handler = new ConversationHistoryRequestHandler(memory, publisher,
            NullLogger<ConversationHistoryRequestHandler>.Instance);
        var request = new ConversationHistoryRequest { SessionId = Thread, IncludeBroadcast = true };

        await handler.HandleAsync(request, new MessageHandlerContext
        {
            Envelope = request.ToEnvelope<ConversationHistoryRequest>(source: "proxy", correlationId: "c-1", replyTo: "history.reply"),
            Agent = new AgentIdentity("TestBot"),
            Services = null!,
            CancellationToken = CancellationToken.None,
        });

        var (topic, envelope) = publisher.Published.Single();
        Assert.AreEqual("history.reply", topic);
        Assert.AreEqual(5, envelope.GetPayload<ConversationHistoryResponse>()!.Turns.Count);
    }

    [TestMethod]
    public async Task ClearContext_HidesEarlierBroadcastOutputFromThatThreadOnly()
    {
        // A cleared thread's next reload must not bring back the old reports, but they are every
        // thread's: another thread keeps them, and a report after the clear shows in both.
        var now = DateTimeOffset.UtcNow;
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "hello", now.AddHours(-1)));
        await memory.AddTurnAsync("other-thread", new ConversationTurn("user", "hi there", now.AddHours(-1)));
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "old report", now.AddMinutes(-30)));
        var clearTimes = new SessionClearTimes();
        var handler = new ClearContextHandler(memory, new StubSessionTracker(), new SessionClientCapabilityStore(),
            new ReplyAttachmentBuffer(), new SessionOriginStore(), new InjectedMemoryTracker(), new TrackingPublisher(),
            new AgentIdentity("TestBot"), NullLogger<ClearContextHandler>.Instance, clearTimes: clearTimes);
        var request = new ClearContextRequest { SessionId = Thread };

        await handler.HandleAsync(request, new MessageHandlerContext
        {
            Envelope = request.ToEnvelope<ClearContextRequest>(source: "proxy"),
            Agent = new AgentIdentity("TestBot"),
            Services = null!,
            CancellationToken = CancellationToken.None,
        });
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "new report", now.AddMinutes(1)));

        var cleared = await ConversationHistoryRequestHandler.ReadTurnsAsync(memory,
            new ConversationHistoryRequest { SessionId = Thread, IncludeBroadcast = true }, CancellationToken.None,
            clearTimes.Get(Thread));
        var other = await ConversationHistoryRequestHandler.ReadTurnsAsync(memory,
            new ConversationHistoryRequest { SessionId = "other-thread", IncludeBroadcast = true }, CancellationToken.None,
            clearTimes.Get("other-thread"));

        CollectionAssert.AreEqual(new[] { "new report" }, cleared.Select(t => t.Content).ToArray());
        CollectionAssert.AreEqual(new[] { "hi there", "old report", "new report" }, other.Select(t => t.Content).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_IncludesA2AInboundSubSessions()
    {
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "hello", T0));
        await memory.AddTurnAsync("a2a-inbound/peer", new ConversationTurn("assistant", "peer digest", T0.AddMinutes(1)));
        await memory.AddTurnAsync("patrol/daily", new ConversationTurn("user", "not broadcast", T0.AddMinutes(2)));

        var turns = await Read(memory, includeBroadcast: true);

        CollectionAssert.AreEqual(new[] { "hello", "peer digest" }, turns.Select(t => t.Content).ToArray());
        Assert.AreEqual("a2a-inbound/peer", turns[1].SessionId);
    }

    [TestMethod]
    public async Task ReadTurns_WithTheFlag_DropsAnImportFromABroadcastSession()
    {
        // #841: the copy is redundant when the broadcast original is merged in.
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "daily report", T0) { Id = "r1" });
        await memory.AddTurnAsync(Thread, new ConversationTurn("assistant", "daily report", T0.AddMinutes(1))
        {
            Id = "copy",
            ImportedFrom = new ConversationTurnReference(WellKnownSessions.Scheduled, "r1"),
        });
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "summarize that", T0.AddMinutes(1)));

        var merged = await Read(memory, includeBroadcast: true);
        var own = await Read(memory, includeBroadcast: false);

        CollectionAssert.AreEqual(new[] { "r1", null }, merged.Select(t => t.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "copy", null }, own.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_KeepsAnImportWhoseOriginalPredatesAClear()
    {
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "daily report", T0) { Id = "r1" });
        await memory.AddTurnAsync(Thread, new ConversationTurn("assistant", "daily report", T0.AddMinutes(10))
        {
            Id = "copy",
            ImportedFrom = new ConversationTurnReference(WellKnownSessions.Scheduled, "r1"),
        });

        var turns = await ConversationHistoryRequestHandler.ReadTurnsAsync(memory,
            new ConversationHistoryRequest { SessionId = Thread, IncludeBroadcast = true }, CancellationToken.None,
            clearedAt: T0.AddMinutes(5));

        CollectionAssert.AreEqual(new[] { "copy" }, turns.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public async Task ReadTurns_WithTheFlag_KeepsAnImportFromAnotherThread()
    {
        var memory = new DictionaryConversationMemory();
        var origin = new ConversationTurnOrigin("cli", "research it", T0, "cli-session");
        await memory.AddTurnAsync(Thread, new ConversationTurn("assistant", "the result", T0)
        {
            Id = "copy",
            Origin = origin,
            Source = new ConversationTurnSource(ReplySourceKind.Primary),
            ImportedFrom = new ConversationTurnReference("cli-session", "r1"),
        });

        var turn = (await Read(memory, includeBroadcast: true)).Single();

        Assert.AreEqual("copy", turn.Id);
        Assert.AreEqual(Thread, turn.SessionId, "the copy is stored in the thread");
        Assert.AreEqual("cli-session", turn.ImportedFromSessionId);
        Assert.AreEqual(new ReplyOrigin("cli", "research it", T0, "cli-session"), turn.Origin);
    }

    private static Task<IReadOnlyList<ConversationHistoryTurn>> Read(IConversationMemory memory, bool includeBroadcast) =>
        ConversationHistoryRequestHandler.ReadTurnsAsync(memory,
            new ConversationHistoryRequest { SessionId = Thread, IncludeBroadcast = includeBroadcast },
            CancellationToken.None);

    private static async Task<DictionaryConversationMemory> WithThreadAndReportsAsync()
    {
        var memory = new DictionaryConversationMemory();
        await memory.AddTurnAsync(WellKnownSessions.ScheduledSystem, new ConversationTurn("assistant", "audit alert", T0));
        await memory.AddTurnAsync(Thread, new ConversationTurn("user", "hello", T0.AddMinutes(1)));
        await memory.AddTurnAsync(WellKnownSessions.Scheduled, new ConversationTurn("assistant", "daily report", T0.AddMinutes(2)));
        await memory.AddTurnAsync(Thread, new ConversationTurn("assistant", "hi", T0.AddMinutes(3)));
        await memory.AddTurnAsync(WellKnownSessions.A2AInbound, new ConversationTurn("assistant", "agents reached out", T0.AddMinutes(4)));
        await memory.AddTurnAsync("other-thread", new ConversationTurn("user", "not this one", T0.AddMinutes(5)));
        return memory;
    }

    private sealed class DictionaryConversationMemory : IConversationMemory
    {
        private readonly Dictionary<string, List<ConversationTurn>> _sessions = new(StringComparer.Ordinal);

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var turns))
                _sessions[sessionId] = turns = [];
            turns.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>(_sessions.TryGetValue(sessionId, out var turns) ? [.. turns] : []);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _sessions.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. _sessions.Keys]);
    }
}
