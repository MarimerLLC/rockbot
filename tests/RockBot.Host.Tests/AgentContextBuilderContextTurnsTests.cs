using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="AgentHostOptions.MaxLlmContextTurns"/>, which controls how many of the
/// most recent conversation turns <see cref="AgentContextBuilder"/> replays into the LLM
/// context. It was a hard-coded 20 before; large-context conversational agents need to raise
/// it without a rebuild.
/// </summary>
[TestClass]
public class AgentContextBuilderContextTurnsTests
{
    [TestMethod]
    public void DefaultIs20()
    {
        Assert.AreEqual(20, new AgentHostOptions().MaxLlmContextTurns,
            "Default must stay 20 so existing deployments are unaffected.");
    }

    [TestMethod]
    public async Task DefaultOptions_ReplaysAtMost20Turns()
    {
        var builder = BuildBuilder(new FixedHistoryConversationMemory(turnCount: 60));

        var messages = await builder.BuildAsync("session-default", "current message", CancellationToken.None);

        Assert.AreEqual(20, CountHistoryTurns(messages),
            "With default options only the last 20 turns should reach the model.");
    }

    [TestMethod]
    public async Task RaisedOption_ReplaysThatManyTurns()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 60),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-50", "current message", CancellationToken.None);

        Assert.AreEqual(50, CountHistoryTurns(messages),
            "MaxLlmContextTurns=50 should replay the last 50 turns.");
    }

    [TestMethod]
    public async Task RaisedOption_KeepsTheMostRecentTurns()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 60),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-recency", "current message", CancellationToken.None);
        var replayed = HistoryTurns(messages).ToList();

        // Turns are numbered 0..59; the last 50 are 10..59.
        Assert.AreEqual("turn-10", replayed[0], "Oldest replayed turn should be turn-10.");
        Assert.AreEqual("turn-59", replayed[^1], "Newest replayed turn should be turn-59.");
    }

    [TestMethod]
    public async Task HistoryShorterThanWindow_ReplaysEverything()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 7),
            new AgentHostOptions { MaxLlmContextTurns = 50 });

        var messages = await builder.BuildAsync("session-short", "current message", CancellationToken.None);

        Assert.AreEqual(7, CountHistoryTurns(messages),
            "A window larger than the available history must not pad or throw.");
    }

    // ── #530: out-of-window marker ───────────────────────────────────────────

    [TestMethod]
    public async Task HistoryLongerThanWindow_MarkerStandsJustBeforeTheReplayedTurns()
    {
        var builder = BuildBuilder(
            new FixedHistoryConversationMemory(turnCount: 30),
            new AgentHostOptions { MaxLlmContextTurns = 20 });

        var messages = await builder.BuildAsync("session-marker", "current message", CancellationToken.None);
        var firstTurn = messages.FindIndex(m => m.Text == "turn-10");

        Assert.IsTrue(firstTurn > 0);
        var marker = messages[firstTurn - 1];
        Assert.AreEqual(ChatRole.System, marker.Role);
        Assert.AreEqual(ContextCategory.OutOfWindowRecall, ContextTags.Get(marker));
        StringAssert.StartsWith(marker.Text, "[10 earlier turn(s) of this conversation");
        Assert.AreEqual(1, messages.Count(m => ContextTags.Get(m) == ContextCategory.OutOfWindowRecall));
    }

    [TestMethod]
    public async Task HistoryWithinWindow_HasNoMarker()
    {
        var builder = BuildBuilder(new FixedHistoryConversationMemory(turnCount: 20));

        var messages = await builder.BuildAsync("session-fits", "current message", CancellationToken.None);

        Assert.IsFalse(messages.Any(m => ContextTags.Get(m) == ContextCategory.OutOfWindowRecall));
    }

    [TestMethod]
    public async Task OutOfWindowTurn_MatchingTheMessage_IsQuotedIntoContext()
    {
        var turns = new List<ConversationTurn>
        {
            new("user", "turn-Here's a throwaway confirmation code: XJ-4421.", DateTimeOffset.UtcNow),
            new("assistant", "turn-Noted.", DateTimeOffset.UtcNow),
        };
        turns.AddRange(Enumerable.Range(0, 4).Select(i =>
            new ConversationTurn(i % 2 == 0 ? "user" : "assistant", $"turn-filler-{i}", DateTimeOffset.UtcNow)));
        var builder = BuildBuilder(new ListConversationMemory(turns), new AgentHostOptions { MaxLlmContextTurns = 4 });

        var messages = await builder.BuildAsync("session-recall",
            "What was the throwaway confirmation code I gave you earlier?", CancellationToken.None);

        Assert.IsFalse(HistoryTurns(messages).Any(t => t.Contains("XJ-4421")), "the turn itself is out of the window");
        var marker = messages.Single(m => ContextTags.Get(m) == ContextCategory.OutOfWindowRecall);
        StringAssert.Contains(marker.Text, "> turn-Here's a throwaway confirmation code: XJ-4421.");
    }

    // ── #509: turns evicted past retention ───────────────────────────────────

    [TestMethod]
    public async Task ArchivedTurn_MatchingTheMessage_IsQuotedWithItsPlaceInTheConversation()
    {
        var archived = new List<ConversationTurn>
        {
            new("user", "turn-Here's a throwaway confirmation code: XJ-4421.", DateTimeOffset.UtcNow),
            new("assistant", "turn-Noted.", DateTimeOffset.UtcNow),
        };
        var retained = Enumerable.Range(0, 6)
            .Select(i => new ConversationTurn(i % 2 == 0 ? "user" : "assistant", $"turn-filler-{i}", DateTimeOffset.UtcNow))
            .ToList();
        var builder = BuildBuilder(new ListConversationMemory(retained, archived), new AgentHostOptions { MaxLlmContextTurns = 4 });

        var messages = await builder.BuildAsync("session-archive",
            "What was the throwaway confirmation code I gave you earlier?", CancellationToken.None);

        var marker = messages.Single(m => ContextTags.Get(m) == ContextCategory.OutOfWindowRecall);
        StringAssert.StartsWith(marker.Text, "[4 earlier turn(s) of this conversation");
        StringAssert.Contains(marker.Text, "[turn 1 of 8 · user");
        StringAssert.Contains(marker.Text, "> turn-Here's a throwaway confirmation code: XJ-4421.");
        Assert.AreEqual(4, CountHistoryTurns(messages));
    }

    [TestMethod]
    public async Task ArchivedTurns_GetAMarker_EvenWhenTheWindowCoversEveryRetainedTurn()
    {
        var archived = new List<ConversationTurn> { new("user", "turn-old", DateTimeOffset.UtcNow) };
        var retained = new List<ConversationTurn> { new("assistant", "turn-recent", DateTimeOffset.UtcNow) };
        var builder = BuildBuilder(new ListConversationMemory(retained, archived), new AgentHostOptions { MaxLlmContextTurns = 20 });

        var messages = await builder.BuildAsync("session-archive", "current message", CancellationToken.None);

        var marker = messages.Single(m => ContextTags.Get(m) == ContextCategory.OutOfWindowRecall);
        StringAssert.StartsWith(marker.Text, "[1 earlier turn(s) of this conversation");
        Assert.AreEqual(messages.FindIndex(m => m.Text == "turn-recent") - 1, messages.IndexOf(marker));
    }

    // ── #670: history compaction ─────────────────────────────────────────────

    private static string Long(string label, int chars) =>
        label + " " + new string('x', chars) + " END-" + label;

    [TestMethod]
    public async Task OlderTurns_KeepStartAndEnd_RecentTurnsStayFull()
    {
        var turns = Enumerable.Range(0, 10)
            .Select(i => new ConversationTurn(i % 2 == 0 ? "user" : "assistant", Long($"turn-{i}", 5_000), DateTimeOffset.UtcNow))
            .ToList();
        var builder = BuildBuilder(new ListConversationMemory(turns),
            new AgentHostOptions { HistoryFullTurns = 4, HistoryOlderTurnMaxChars = 1_000 });

        var messages = await builder.BuildAsync("s", "current", CancellationToken.None);
        var replayed = HistoryTurns(messages).ToList();

        Assert.AreEqual(10, replayed.Count);
        for (var i = 0; i < 6; i++)
        {
            Assert.IsTrue(replayed[i].Length < 1_200, $"turn-{i} should be cut");
            StringAssert.Contains(replayed[i], "chars of this earlier turn elided");
            StringAssert.EndsWith(replayed[i], $"END-turn-{i}", "the end of an older turn is kept");
        }
        for (var i = 6; i < 10; i++)
            Assert.AreEqual(turns[i].Content, replayed[i], $"turn-{i} is one of the last 4 and stays full");
    }

    [TestMethod]
    public async Task RelayedSubagentResult_IsShortened_LatestUnansweredRelayStaysFull()
    {
        var turns = new List<ConversationTurn>
        {
            new("user", "turn-ask research it", DateTimeOffset.UtcNow),
            new("assistant", "turn-on it", DateTimeOffset.UtcNow),
            new("user", "turn-" + Long("[Subagent task aaa completed]:", 8_000), DateTimeOffset.UtcNow) { AgentName = "subagent-aaa" },
            new("assistant", "turn-here is the synthesis", DateTimeOffset.UtcNow),
            new("user", "turn-" + Long("[Subagent task bbb completed]:", 8_000), DateTimeOffset.UtcNow) { AgentName = "subagent-bbb" },
        };
        var builder = BuildBuilder(new ListConversationMemory(turns),
            new AgentHostOptions { RelayedSubagentTurnMaxChars = 1_000 });

        var messages = await builder.BuildAsync("s", "current", CancellationToken.None);
        var replayed = HistoryTurns(messages).ToList();

        Assert.IsTrue(replayed[2].Length < 1_400, "the relayed result is cut");
        StringAssert.Contains(replayed[2], "already relayed to the user");
        StringAssert.Contains(replayed[2], "'subagent/aaa/output'");
        Assert.AreEqual(turns[4].Content, replayed[4], "the relay the synthesis is about to answer stays full");
        Assert.AreEqual(ContextCategory.SubagentRelay,
            ContextTags.Get(messages.First(m => m.Text == replayed[2])));
    }

    [TestMethod]
    public async Task ConsolidationNote_IsDroppedOnceAnswered_AndKeptBeforeTheSynthesis()
    {
        var answered = new List<ConversationTurn>
        {
            new("user", "turn-ask", DateTimeOffset.UtcNow),
            new("system", "turn-[All 2 subagent tasks completed. Synthesize]", DateTimeOffset.UtcNow),
            new("assistant", "turn-synthesis", DateTimeOffset.UtcNow),
            new("user", "turn-next", DateTimeOffset.UtcNow),
        };
        var pending = answered.Take(2).ToList();

        var afterAnswer = HistoryTurns(await BuildBuilder(new ListConversationMemory(answered))
            .BuildAsync("s", "current", CancellationToken.None)).ToList();
        var beforeAnswer = HistoryTurns(await BuildBuilder(new ListConversationMemory(pending))
            .BuildAsync("s", "current", CancellationToken.None)).ToList();

        CollectionAssert.AreEqual(new[] { "turn-ask", "turn-synthesis", "turn-next" }, afterAnswer);
        CollectionAssert.Contains(beforeAnswer, "turn-[All 2 subagent tasks completed. Synthesize]");
    }

    [TestMethod]
    public void HeadTail_ShortTextAndDisabledLimit_AreUnchanged()
    {
        Assert.AreEqual("short", HistoryCompaction.HeadTail("short", 100, "{0}"));
        var longText = new string('a', 500);
        Assert.AreEqual(longText, HistoryCompaction.HeadTail(longText, 0, "{0}"));
        StringAssert.Contains(HistoryCompaction.HeadTail(longText, 100, "cut {0}"), "[cut 400]");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Synthetic history turns are the only messages whose text starts with "turn-".</summary>
    private static IEnumerable<string> HistoryTurns(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => m.Text).Where(t => t.StartsWith("turn-", StringComparison.Ordinal));

    private static int CountHistoryTurns(IReadOnlyList<ChatMessage> messages) => HistoryTurns(messages).Count();

    private static AgentContextBuilder BuildBuilder(
        IConversationMemory conversationMemory,
        AgentHostOptions? hostOptions = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-ctxturns-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: conversationMemory,
            longTermMemory: new StubLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: new StubSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance,
            agentHostOptions: hostOptions is null ? null : Options.Create(hostOptions));
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    /// <summary>Returns <paramref name="turnCount"/> alternating turns named <c>turn-{i}</c>.</summary>
    private sealed class FixedHistoryConversationMemory(int turnCount) : IConversationMemory
    {
        private readonly IReadOnlyList<ConversationTurn> _turns =
            [.. Enumerable.Range(0, turnCount).Select(i =>
                new ConversationTurn(i % 2 == 0 ? "user" : "assistant", $"turn-{i}", DateTimeOffset.UtcNow))];

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_turns);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ListConversationMemory(
        IReadOnlyList<ConversationTurn> turns, IReadOnlyList<ConversationTurn>? archived = null) : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(turns);

        public Task<IReadOnlyList<ConversationTurn>> GetArchivedTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(archived ?? []);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
