using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Observation;

namespace RockBot.Host.Tests;

[TestClass]
public class ConversationLogTranscriptAdapterTests
{
    private static ConversationLogEntry Entry(
        string sessionId, string role, string content, DateTimeOffset? timestamp = null) =>
        new(sessionId, role, content, timestamp ?? DateTimeOffset.UtcNow);

    private ConversationLogTranscriptAdapter Adapter(IConversationLog log) =>
        new(log, NullLogger<ConversationLogTranscriptAdapter>.Instance);

    [TestMethod]
    public async Task GetTranscriptAsync_EmptyLog_ReturnsEmpty()
    {
        var log = new InMemoryConversationLog();
        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);
        Assert.AreEqual(0, turns.Count);
    }

    [TestMethod]
    public async Task GetTranscriptAsync_UserSession_MapsRolesToSources()
    {
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("session/blazor-session", "user", "hello", t0),
                Entry("session/blazor-session", "assistant", "hi", t0.AddSeconds(1)),
                Entry("session/blazor-session", "system", "note", t0.AddSeconds(2)),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        Assert.AreEqual(3, turns.Count);
        Assert.AreEqual(TranscriptSources.User, turns[0].Source);
        Assert.AreEqual(TranscriptSources.Agent, turns[1].Source,
            "Assistant role in a user session maps to Agent source");
        Assert.AreEqual(TranscriptSources.Agent, turns[2].Source,
            "System role in a user session maps to Agent source");
    }

    [TestMethod]
    public async Task GetTranscriptAsync_PatrolSession_MapsAllToScheduledTask()
    {
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("patrol/heartbeat", "user", "(scheduled trigger)"),
                Entry("patrol/heartbeat", "assistant", "did the patrol"),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        Assert.AreEqual(2, turns.Count);
        Assert.IsTrue(turns.All(t => t.Source == TranscriptSources.ScheduledTask),
            "All patrol turns regardless of role map to ScheduledTask source");
    }

    [TestMethod]
    public async Task GetTranscriptAsync_A2AInbound_MapsToAgent()
    {
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("a2a-inbound/abc-123", "user", "calling agent"),
                Entry("a2a-inbound/abc-123", "assistant", "responded"),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        Assert.AreEqual(2, turns.Count);
        Assert.IsTrue(turns.All(t => t.Source == TranscriptSources.Agent),
            "A2A inbound turns map to Agent source — not human user signal");
    }

    [TestMethod]
    public async Task GetTranscriptAsync_TurnIdsStableWithinSession()
    {
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("session/a", "user", "first", DateTimeOffset.Parse("2026-05-08T10:00:00Z")),
                Entry("session/a", "assistant", "second", DateTimeOffset.Parse("2026-05-08T10:00:01Z")),
                Entry("session/a", "user", "third", DateTimeOffset.Parse("2026-05-08T10:00:02Z")),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        var ids = turns.Select(t => t.TurnId).ToArray();
        CollectionAssert.AreEqual(new[] { "t0", "t1", "t2" }, ids);
    }

    [TestMethod]
    public async Task GetTranscriptAsync_OrdersByTimestampWithinSession()
    {
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        // Insert in reverse-chronological order; adapter must reorder by timestamp.
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("session/a", "user", "third", t0.AddSeconds(2)),
                Entry("session/a", "user", "first", t0),
                Entry("session/a", "user", "second", t0.AddSeconds(1)),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "first", "second", "third" },
            turns.Select(t => t.Content).ToArray());
    }

    [TestMethod]
    public async Task GetTranscriptAsync_MultipleSessions_AllAppear()
    {
        var log = new InMemoryConversationLog
        {
            Entries =
            {
                Entry("session/a", "user", "from a"),
                Entry("patrol/heartbeat", "user", "from patrol"),
                Entry("a2a-inbound/x", "user", "from a2a"),
            },
        };

        var turns = await Adapter(log).GetTranscriptAsync(CancellationToken.None);

        Assert.AreEqual(3, turns.Count);
        var sessions = turns.Select(t => t.ConversationId).Distinct().ToArray();
        Assert.AreEqual(3, sessions.Length);
    }

    [TestMethod]
    public async Task GetTranscriptAsync_HonorsCancellationToken()
    {
        var log = new InMemoryConversationLog { ThrowOnRead = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Adapter(log).GetTranscriptAsync(cts.Token));
    }

    private sealed class InMemoryConversationLog : IConversationLog
    {
        public List<ConversationLogEntry> Entries { get; } = [];
        public bool ThrowOnRead { get; set; }

        public Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new InvalidOperationException("test failure");
            return Task.FromResult<IReadOnlyList<ConversationLogEntry>>(Entries.ToList());
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            return Task.CompletedTask;
        }
    }
}
