using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.UserProxy;

namespace RockBot.Host.Tests;

/// <summary>
/// #809: stored turns carry an id, an origin and a source, and broadcast replies are stored in
/// their own sessions, so a client that reloads history can rebuild what it saw live.
/// </summary>
[TestClass]
public class ConversationTurnProvenanceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rockbot-provenance-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static InMemoryConversationMemory CreateInMemory() =>
        new(Options.Create(new ConversationMemoryOptions()), NullLogger<InMemoryConversationMemory>.Instance);

    private FileConversationMemory CreateFileMemory()
    {
        var options = Options.Create(new ConversationMemoryOptions { BasePath = "conversations" });
        return new FileConversationMemory(
            new InMemoryConversationMemory(options, NullLogger<InMemoryConversationMemory>.Instance),
            options,
            Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<FileConversationMemory>.Instance);
    }

    private static readonly ReplyOrigin Origin =
        new("blazor", "research the thing", new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero), "blazor-session");

    // ── Ids ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AddTurn_WithoutId_StoresTurnWithNewId()
    {
        using var memory = CreateInMemory();

        await memory.AddTurnAsync("s", new ConversationTurn("user", "one", DateTimeOffset.UtcNow));
        await memory.AddTurnAsync("s", new ConversationTurn("user", "two", DateTimeOffset.UtcNow));

        var turns = await memory.GetTurnsAsync("s");
        Assert.IsFalse(string.IsNullOrEmpty(turns[0].Id));
        Assert.IsFalse(string.IsNullOrEmpty(turns[1].Id));
        Assert.AreNotEqual(turns[0].Id, turns[1].Id);
    }

    [TestMethod]
    public async Task AddTurn_WithId_KeepsIt()
    {
        using var memory = CreateInMemory();

        await memory.AddTurnAsync("s", new ConversationTurn("assistant", "hi", DateTimeOffset.UtcNow) { Id = "reply-1" });

        Assert.AreEqual("reply-1", (await memory.GetTurnsAsync("s"))[0].Id);
    }

    [TestMethod]
    public async Task FileMemory_RoundTripsIdOriginAndSource()
    {
        using (var memory = CreateFileMemory())
        {
            await memory.AddTurnAsync("blazor-session", new ConversationTurn("user", "result", DateTimeOffset.UtcNow)
            {
                Id = "turn-1",
                AgentName = "subagent-abc",
                Origin = Origin.ToTurnOrigin(),
                Source = new ConversationTurnSource(ReplySourceKind.Subagent, "abc"),
            });
        }

        using var restarted = CreateFileMemory();
        await restarted.StartAsync(CancellationToken.None);

        var turn = (await restarted.GetTurnsAsync("blazor-session")).Single();
        Assert.AreEqual("turn-1", turn.Id);
        Assert.AreEqual(Origin.ToTurnOrigin(), turn.Origin);
        Assert.AreEqual(new ConversationTurnSource(ReplySourceKind.Subagent, "abc"), turn.Source);
    }

    [TestMethod]
    public async Task FileMemory_LegacyTurnsWithoutIds_GetIdsThatHoldAcrossRestarts()
    {
        // A session file written before turns had ids, origins or sources.
        var file = Path.Combine(_tempDir, "conversations", "blazor-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var at = DateTimeOffset.UtcNow.ToString("O");
        await File.WriteAllTextAsync(file,
            $$"""[{"role":"user","content":"hello","timestamp":"{{at}}"},{"role":"assistant","content":"hi","timestamp":"{{at}}","agentName":"RockBot"}]""");

        string?[] first;
        using (var memory = CreateFileMemory())
        {
            await memory.StartAsync(CancellationToken.None);
            var turns = await memory.GetTurnsAsync("blazor-session");
            Assert.AreEqual(2, turns.Count);
            Assert.IsNull(turns[0].Origin);
            Assert.IsNull(turns[0].Source);
            first = [.. turns.Select(t => t.Id)];
        }
        Assert.IsTrue(first.All(id => !string.IsNullOrEmpty(id)));

        using var again = CreateFileMemory();
        await again.StartAsync(CancellationToken.None);
        CollectionAssert.AreEqual(first, (await again.GetTurnsAsync("blazor-session")).Select(t => t.Id).ToArray());
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Matching_TakesTheReplysIdOriginAndSource()
    {
        var reply = new AgentReply
        {
            Content = "done", SessionId = "blazor-session", AgentName = "RockBot",
            MessageId = "m-1", Origin = Origin, Source = ReplySource.A2A,
        };

        var turn = new ConversationTurn("assistant", "done", DateTimeOffset.UtcNow) { AgentName = "RockBot" }.Matching(reply);

        Assert.AreEqual("m-1", turn.Id);
        Assert.AreEqual(Origin, turn.Origin.ToReplyOrigin());
        Assert.AreEqual(ReplySourceKind.A2A, turn.Source?.Kind);
        Assert.AreEqual("RockBot", turn.AgentName);
    }

    [TestMethod]
    public void Matching_KeepsTheTurnsOwnValues_WhereTheReplyHasNone()
    {
        var reply = new AgentReply { Content = "x", SessionId = "s", AgentName = "RockBot" };
        var source = new ConversationTurnSource(ReplySourceKind.Bridge);

        var turn = new ConversationTurn("user", "x", DateTimeOffset.UtcNow) { Id = "own", Source = source }.Matching(reply);

        Assert.AreEqual("own", turn.Id);
        Assert.AreEqual(source, turn.Source);
        Assert.IsNull(turn.Origin);
    }

    [TestMethod]
    public void IsBroadcast_NamesTheThreeBroadcastSessions()
    {
        Assert.IsTrue(WellKnownSessions.IsBroadcast("scheduled"));
        Assert.IsTrue(WellKnownSessions.IsBroadcast("scheduled-system"));
        Assert.IsTrue(WellKnownSessions.IsBroadcast("a2a-inbound"));
        Assert.IsFalse(WellKnownSessions.IsBroadcast("blazor-session"));
        Assert.IsFalse(WellKnownSessions.IsBroadcast("patrol/daily"));
        Assert.IsFalse(WellKnownSessions.IsBroadcast(null));
        CollectionAssert.AreEquivalent(new[] { "scheduled", "scheduled-system", "a2a-inbound" }, WellKnownSessions.Broadcast.ToArray());
    }

    // ── Broadcast replies ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task RecordBroadcastReply_StoresTheReplyInItsSession_WithItsId()
    {
        using var memory = CreateInMemory();
        var reply = new AgentReply
        {
            Content = "**Daily summary**", SessionId = WellKnownSessions.Scheduled, AgentName = "RockBot",
            Source = ReplySource.Scheduled, Origin = Origin with { Channel = "scheduled", SessionId = "scheduled" },
            Attachments = [new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Chart" }],
        };

        var sent = await memory.RecordBroadcastReplyAsync(reply, NullLogger.Instance);

        Assert.IsFalse(string.IsNullOrEmpty(sent.MessageId), "the reply gets the id its stored copy has");
        var turn = (await memory.GetTurnsAsync(WellKnownSessions.Scheduled)).Single();
        Assert.AreEqual("assistant", turn.Role);
        Assert.AreEqual(reply.Content, turn.Content);
        Assert.AreEqual(sent.MessageId, turn.Id);
        Assert.AreEqual("RockBot", turn.AgentName);
        Assert.AreEqual(ReplySourceKind.Scheduled, turn.Source?.Kind);
        Assert.AreEqual(reply.Origin, turn.Origin.ToReplyOrigin());
        Assert.AreEqual(new ConversationTurnAttachment("image/png", "chart.png", "Chart"), turn.Attachments!.Single());
    }

    [TestMethod]
    public async Task RecordBroadcastReply_KeepsAnIdTheReplyAlreadyHas()
    {
        using var memory = CreateInMemory();
        var reply = new AgentReply
        {
            Content = "x", SessionId = WellKnownSessions.A2AInbound, AgentName = "A2A-Inbox", MessageId = "given",
        };

        var sent = await memory.RecordBroadcastReplyAsync(reply, NullLogger.Instance);

        Assert.AreEqual("given", sent.MessageId);
        Assert.AreEqual("given", (await memory.GetTurnsAsync(WellKnownSessions.A2AInbound)).Single().Id);
    }

    [TestMethod]
    public async Task RecordBroadcastReply_OtherSession_StoresNothing()
    {
        using var memory = CreateInMemory();
        var reply = new AgentReply { Content = "x", SessionId = "blazor-session", AgentName = "RockBot" };

        var sent = await memory.RecordBroadcastReplyAsync(reply, NullLogger.Instance);

        Assert.AreSame(reply, sent);
        Assert.AreEqual(0, (await memory.GetTurnsAsync("blazor-session")).Count);
    }

    [TestMethod]
    public async Task RecordBroadcastReply_NoMemory_ReturnsTheReply()
    {
        IConversationMemory? memory = null;
        var reply = new AgentReply { Content = "x", SessionId = WellKnownSessions.Scheduled, AgentName = "RockBot" };

        Assert.AreSame(reply, await memory.RecordBroadcastReplyAsync(reply, NullLogger.Instance));
    }
}
