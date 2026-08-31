using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class InMemoryConversationMemoryTests
{
    private InMemoryConversationMemory _memory = null!;

    [TestInitialize]
    public void Setup()
    {
        _memory = CreateMemory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _memory.Dispose();
    }

    [TestMethod]
    public async Task AddTurnAsync_And_GetTurnsAsync_ReturnsTurns()
    {
        var turn1 = new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow);
        var turn2 = new ConversationTurn("assistant", "Hi there!", DateTimeOffset.UtcNow);

        await _memory.AddTurnAsync("session-1", turn1);
        await _memory.AddTurnAsync("session-1", turn2);

        var turns = await _memory.GetTurnsAsync("session-1");

        Assert.AreEqual(2, turns.Count);
        Assert.AreEqual("user", turns[0].Role);
        Assert.AreEqual("Hello", turns[0].Content);
        Assert.AreEqual("assistant", turns[1].Role);
        Assert.AreEqual("Hi there!", turns[1].Content);
    }

    [TestMethod]
    public async Task GetTurnsAsync_UnknownSession_ReturnsEmptyList()
    {
        var turns = await _memory.GetTurnsAsync("nonexistent");

        Assert.AreEqual(0, turns.Count);
    }

    [TestMethod]
    public async Task SlidingWindow_EvictsOldestTurns()
    {
        _memory.Dispose();
        _memory = CreateMemory(maxTurns: 3);

        for (int i = 1; i <= 5; i++)
        {
            await _memory.AddTurnAsync("session-1",
                new ConversationTurn("user", $"Message {i}", DateTimeOffset.UtcNow));
        }

        var turns = await _memory.GetTurnsAsync("session-1");

        Assert.AreEqual(3, turns.Count);
        Assert.AreEqual("Message 3", turns[0].Content);
        Assert.AreEqual("Message 4", turns[1].Content);
        Assert.AreEqual("Message 5", turns[2].Content);
    }

    [TestMethod]
    public async Task ClearAsync_RemovesSession()
    {
        await _memory.AddTurnAsync("session-1",
            new ConversationTurn("user", "Hello", DateTimeOffset.UtcNow));

        await _memory.ClearAsync("session-1");

        var turns = await _memory.GetTurnsAsync("session-1");
        Assert.AreEqual(0, turns.Count);
    }

    [TestMethod]
    public async Task ClearAsync_NonexistentSession_NoOp()
    {
        // Should not throw
        await _memory.ClearAsync("nonexistent");
    }

    [TestMethod]
    public async Task MultipleSessions_AreIsolated()
    {
        await _memory.AddTurnAsync("session-1",
            new ConversationTurn("user", "Hello from 1", DateTimeOffset.UtcNow));
        await _memory.AddTurnAsync("session-2",
            new ConversationTurn("user", "Hello from 2", DateTimeOffset.UtcNow));

        var turns1 = await _memory.GetTurnsAsync("session-1");
        var turns2 = await _memory.GetTurnsAsync("session-2");

        Assert.AreEqual(1, turns1.Count);
        Assert.AreEqual("Hello from 1", turns1[0].Content);
        Assert.AreEqual(1, turns2.Count);
        Assert.AreEqual("Hello from 2", turns2[0].Content);
    }

    [TestMethod]
    public async Task GetTurnsAsync_ReturnsSnapshot_NotLiveReference()
    {
        await _memory.AddTurnAsync("session-1",
            new ConversationTurn("user", "First", DateTimeOffset.UtcNow));

        var turns = await _memory.GetTurnsAsync("session-1");

        // Add another turn after getting the snapshot
        await _memory.AddTurnAsync("session-1",
            new ConversationTurn("user", "Second", DateTimeOffset.UtcNow));

        // Original snapshot should be unchanged
        Assert.AreEqual(1, turns.Count);
    }

    private static InMemoryConversationMemory CreateMemory(int maxTurns = 50, TimeSpan? idleTimeout = null)
    {
        var options = new ConversationMemoryOptions
        {
            MaxTurnsPerSession = maxTurns,
            SessionIdleTimeout = idleTimeout ?? TimeSpan.FromHours(1)
        };

        return new InMemoryConversationMemory(
            Options.Create(options),
            NullLogger<InMemoryConversationMemory>.Instance);
    }
}
