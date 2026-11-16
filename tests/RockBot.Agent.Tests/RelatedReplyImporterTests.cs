using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>
/// #841: the turns a message refers to are copied into its session before it is answered.
/// </summary>
[TestClass]
public class RelatedReplyImporterTests
{
    private const string Here = "blazor-session";
    private const string There = "cli-session";
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = T0.AddHours(1);

    private ArchivingConversationMemory _memory = null!;
    private SessionOriginStore _origins = null!;
    private RelatedReplyImporter _importer = null!;

    [TestInitialize]
    public void Init()
    {
        _memory = new ArchivingConversationMemory();
        _origins = new SessionOriginStore();
        _importer = new RelatedReplyImporter(_memory, _origins, NullLogger<RelatedReplyImporter>.Instance, new FixedTime(Now));
    }

    [TestMethod]
    public async Task FoundInLiveTurns_IsCopiedWithProvenance()
    {
        var origin = new ConversationTurnOrigin("cli", "research X", T0, There);
        await _memory.AddTurnAsync(There, new ConversationTurn("assistant", "the result", T0)
        {
            Id = "r1",
            AgentName = "RockBot",
            Origin = origin,
            Source = new ConversationTurnSource(ReplySourceKind.Subagent, "task-1"),
            Attachments = [new ConversationTurnAttachment("image/png", "a/b.png")],
        });

        var count = await _importer.ImportAsync(Message(new ReplyReference(There, "r1")));

        Assert.AreEqual(1, count);
        var copy = (await _memory.GetTurnsAsync(Here)).Single();
        Assert.AreEqual("assistant", copy.Role);
        Assert.AreEqual("the result", copy.Content);
        Assert.AreEqual("RockBot", copy.AgentName);
        Assert.AreEqual(origin, copy.Origin);
        Assert.AreEqual(new ConversationTurnSource(ReplySourceKind.Subagent, "task-1"), copy.Source);
        Assert.AreEqual(1, copy.Attachments?.Count);
        Assert.AreEqual(new ConversationTurnReference(There, "r1"), copy.ImportedFrom);
        Assert.AreEqual(Now, copy.Timestamp);
        Assert.IsNotNull(copy.Id);
        Assert.AreNotEqual("r1", copy.Id);
    }

    [TestMethod]
    public async Task FoundInArchive_IsCopied()
    {
        _memory.Archive(There, new ConversationTurn("assistant", "old result", T0) { Id = "r1" });

        var count = await _importer.ImportAsync(Message(new ReplyReference(There, "r1")));

        Assert.AreEqual(1, count);
        Assert.AreEqual("old result", (await _memory.GetTurnsAsync(Here)).Single().Content);
    }

    [TestMethod]
    public async Task Missing_IsSkipped()
    {
        var count = await _importer.ImportAsync(Message(new ReplyReference(There, "nope")));

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, (await _memory.GetTurnsAsync(Here)).Count);
    }

    [TestMethod]
    public async Task OwnSession_IsSkipped()
    {
        await _memory.AddTurnAsync(Here, new ConversationTurn("assistant", "mine", T0) { Id = "r1" });

        var count = await _importer.ImportAsync(Message(new ReplyReference("session/" + Here, "r1")));

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, (await _memory.GetTurnsAsync(Here)).Count);
    }

    [TestMethod]
    public async Task AlreadyImported_IsSkipped()
    {
        await _memory.AddTurnAsync(There, new ConversationTurn("assistant", "the result", T0) { Id = "r1" });

        await _importer.ImportAsync(Message(new ReplyReference(There, "r1")));
        var again = await _importer.ImportAsync(Message(new ReplyReference(There, "r1"), new ReplyReference(There, "r1")));

        Assert.AreEqual(0, again);
        Assert.AreEqual(1, (await _memory.GetTurnsAsync(Here)).Count);
    }

    [TestMethod]
    public async Task LongContent_IsTruncated()
    {
        await _memory.AddTurnAsync(There, new ConversationTurn("assistant", new string('x', 20_000), T0) { Id = "r1" });

        await _importer.ImportAsync(Message(new ReplyReference(There, "r1")));

        var copy = (await _memory.GetTurnsAsync(Here)).Single();
        Assert.AreEqual(RelatedReplyImporter.MaxContentChars + RelatedReplyImporter.TruncationNote.Length, copy.Content.Length);
        Assert.IsTrue(copy.Content.EndsWith(RelatedReplyImporter.TruncationNote, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NoOrigin_TakesTheSourceSessionsOriginFromTheStore()
    {
        await _memory.AddTurnAsync(There, new ConversationTurn("assistant", "the result", T0) { Id = "r1" });
        _origins.Set(There, new ReplyOrigin("cli", "research X", T0, There));

        await _importer.ImportAsync(Message(new ReplyReference(There, "r1")));

        Assert.AreEqual(new ConversationTurnOrigin("cli", "research X", T0, There), (await _memory.GetTurnsAsync(Here)).Single().Origin);
    }

    [TestMethod]
    public async Task AtMostFive_AreImported()
    {
        var references = new List<ReplyReference>();
        for (var i = 0; i < 7; i++)
        {
            await _memory.AddTurnAsync(There, new ConversationTurn("assistant", $"r{i}", T0) { Id = $"r{i}" });
            references.Add(new ReplyReference(There, $"r{i}"));
        }

        var count = await _importer.ImportAsync(Message([.. references]));

        Assert.AreEqual(RelatedReplyImporter.MaxImports, count);
    }

    [TestMethod]
    public async Task NoReferences_DoesNothing()
    {
        Assert.AreEqual(0, await _importer.ImportAsync(new UserMessage { Content = "hi", SessionId = Here, UserId = "u" }));
    }

    private static UserMessage Message(params ReplyReference[] references) => new()
    {
        Content = "summarize that",
        SessionId = Here,
        UserId = "u",
        RelatedReplies = references,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ArchivingConversationMemory : IConversationMemory
    {
        private readonly Dictionary<string, List<ConversationTurn>> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ConversationTurn>> _archives = new(StringComparer.Ordinal);

        public void Archive(string sessionId, ConversationTurn turn)
        {
            if (!_archives.TryGetValue(sessionId, out var turns))
                _archives[sessionId] = turns = [];
            turns.Add(turn);
        }

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var turns))
                _sessions[sessionId] = turns = [];
            turns.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>(_sessions.TryGetValue(sessionId, out var turns) ? [.. turns] : []);

        public Task<IReadOnlyList<ConversationTurn>> GetArchivedTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>(_archives.TryGetValue(sessionId, out var turns) ? [.. turns] : []);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _sessions.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. _sessions.Keys]);
    }
}
