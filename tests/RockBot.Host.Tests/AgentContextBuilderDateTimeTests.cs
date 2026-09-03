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
/// Guards the prompt-cache invariant: <see cref="AgentContextBuilder"/> must not inject a datetime
/// system message. Datetime context comes from <c>AgentLoopRunner.EnsureDateTimeContext</c>, which
/// rounds to the minute and inserts just before the last user message so the whole prefix ahead of it
/// stays byte-identical across turns. A datetime message here sits right behind the system prompt, so
/// every request diverges from the end of the persona onward — a few thousand tokens of needless
/// recompute on cloud providers, and the entire prompt on llama.cpp with sliding-window attention,
/// which cannot roll its cache back mid-sequence.
/// </summary>
[TestClass]
public class AgentContextBuilderDateTimeTests
{
    [TestMethod]
    public async Task BuildAsync_InjectsNoDateTimeMessage()
    {
        var builder = BuildBuilder();

        var messages = await builder.BuildAsync("session-datetime", "current message", CancellationToken.None);

        AssertNoDateTime(messages);
    }

    [TestMethod]
    public async Task BuildForWorkerAsync_InjectsNoDateTimeMessage()
    {
        var builder = BuildBuilder();

        var messages = await builder.BuildForWorkerAsync(
            "worker-datetime", "do the thing", CancellationToken.None, "worker/abc", "worker system prompt");

        AssertNoDateTime(messages);
    }

    [TestMethod]
    public async Task BuildAsync_SystemPromptIsTheFirstMessage()
    {
        var builder = BuildBuilder();

        var messages = await builder.BuildAsync("session-first", "current message", CancellationToken.None);

        Assert.AreEqual(ChatRole.System, messages[0].Role);
        StringAssert.Contains(messages[0].Text, "I am a test agent.",
            "The agent profile must remain the very first message so it anchors the cacheable prefix.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertNoDateTime(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var text = message.Text ?? string.Empty;
            Assert.IsFalse(text.Contains("date and time", StringComparison.OrdinalIgnoreCase),
                $"AgentContextBuilder must not inject datetime context — it busts the prompt cache. Found: {text}");
            Assert.IsFalse(text.Contains("UTC equivalent", StringComparison.OrdinalIgnoreCase),
                $"AgentContextBuilder must not inject datetime context — it busts the prompt cache. Found: {text}");
        }
    }

    private static AgentContextBuilder BuildBuilder()
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-datetime-test-" + Guid.NewGuid().ToString("N"))
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
            conversationMemory: new StubConversationMemory(),
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
            logger: NullLogger<AgentContextBuilder>.Instance);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);

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
