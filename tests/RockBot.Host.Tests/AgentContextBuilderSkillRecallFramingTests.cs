using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the auto-recalled skill framing in <see cref="AgentContextBuilder"/>
/// (issue #492). A skill the builder injects on its own initiative — via per-turn
/// BM25/hybrid recall, not because the agent called <c>get_skill</c> — must arrive
/// labelled as reference material rather than as a bare system message that reads
/// like a directive. The production incident: a one-line "add a todo" request
/// recalled a calendar briefing playbook and the model executed it, spawning six
/// workers before making the single tool call the user asked for.
/// </summary>
[TestClass]
public class AgentContextBuilderSkillRecallFramingTests
{
    // Long enough to clear ShortMessageHeuristics.UserMessageCharThreshold so the
    // recall path actually runs.
    private const string LongMessage =
        "Please add a todo for Friday for me to send a W9 to Kelly for the podcast series.";

    /// <summary>The #492 reproducer: an imperative briefing playbook, the shape that got executed.</summary>
    private static Skill BriefingSkill() => new(
        "calendar-todo-remainder-briefing",
        "Build a verified calendar/todo readiness pack across all accounts.",
        "## Steps\n1. Call list_accounts.\n2. Spawn one worker per account for next-24h events.\n3. Detect conflicts.",
        DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task RecalledSkill_IsInjectedWithNotAnInstructionFraming()
    {
        var skill = BriefingSkill();
        var (builder, _) = BuildBuilder(skillStore: new StubSkillStore(search: [skill]));

        var messages = await builder.BuildAsync("session-framing", LongMessage, CancellationToken.None);

        var injected = FindSystemMessageContaining(messages, $"Skill: {skill.Name}");
        Assert.IsNotNull(injected, "The rank-1 recalled skill body should still be injected.");
        Assert.IsTrue(injected.StartsWith(SkillRecallFraming.Preamble, StringComparison.Ordinal),
            "An auto-recalled skill body must be prefixed with the recall framing preamble.");
        StringAssert.Contains(injected, skill.Content,
            "Framing must not displace the skill body itself.");
    }

    [TestMethod]
    public async Task RecallFraming_SaysReferenceMaterialNotInstruction()
    {
        var (builder, _) = BuildBuilder(skillStore: new StubSkillStore(search: [BriefingSkill()]));

        var messages = await builder.BuildAsync("session-framing-wording", LongMessage, CancellationToken.None);

        var injected = FindSystemMessageContaining(messages, "Skill: calendar-todo-remainder-briefing");
        Assert.IsNotNull(injected);

        // The regression this guards is intent, not phrasing: the prompt has to deny
        // both "this is an instruction" and "this justifies fanning out".
        StringAssert.Contains(injected, "NOT AN INSTRUCTION",
            "The framing must explicitly deny that a recalled skill is an instruction.");
        StringAssert.Contains(injected, "spawning workers",
            "The framing must warn against widening scope into worker fan-out.");
    }

    [TestMethod]
    public async Task NoRecalledSkills_InjectsNoFraming()
    {
        var (builder, _) = BuildBuilder(skillStore: new StubSkillStore(search: []));

        var messages = await builder.BuildAsync("session-framing-none", LongMessage, CancellationToken.None);

        Assert.IsNull(FindSystemMessageContaining(messages, SkillRecallFraming.Preamble),
            "With no recalled skills there is nothing to frame.");
    }

    [TestMethod]
    public async Task LowerRankedSkills_AreOfferedAsOptional()
    {
        var skills = new[]
        {
            BriefingSkill(),
            new Skill("second-skill", "A second summary.", "second body", DateTimeOffset.UtcNow)
        };
        var (builder, _) = BuildBuilder(skillStore: new StubSkillStore(search: skills));

        var messages = await builder.BuildAsync("session-framing-ranks", LongMessage, CancellationToken.None);

        var summaryBlock = FindSystemMessageContaining(messages, "second-skill: A second summary.");
        Assert.IsNotNull(summaryBlock, "Ranks 2+ should still be offered as summaries.");
        StringAssert.Contains(summaryBlock, "None of them is a directive",
            "The summary block must present lower-ranked skills as optional, not as pending work.");
    }

    [TestMethod]
    public async Task WorkerContext_FramesRecalledSkillsToo()
    {
        var skill = BriefingSkill();
        var (builder, _) = BuildBuilder(skillStore: new StubSkillStore(search: [skill]));

        var messages = await builder.BuildForWorkerAsync(
            "worker/framing-1",
            LongMessage,
            CancellationToken.None,
            workingMemoryNamespace: "worker/framing-1",
            systemPromptOverride: "You are a worker.");

        var injected = FindSystemMessageContaining(messages, $"Skill: {skill.Name}");
        Assert.IsNotNull(injected, "Workers still get recalled skill bodies.");
        Assert.IsTrue(injected.StartsWith(SkillRecallFraming.Preamble, StringComparison.Ordinal),
            "Worker skill recall must carry the same not-an-instruction framing as the main path.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? FindSystemMessageContaining(IEnumerable<ChatMessage> messages, string fragment)
    {
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System && m.Text is { } t && t.Contains(fragment, StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private static (AgentContextBuilder Builder, KnowledgeGraphOptions Options) BuildBuilder(
        ISkillStore? skillStore = null)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-skillframing-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        var graphOpts = new KnowledgeGraphOptions();

        var builder = new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new StubConversationMemory(),
            longTermMemory: new StubLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: skillStore ?? new StubSkillStore(search: []),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(graphOpts),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance);

        return (builder, graphOpts);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class StubSkillStore(IReadOnlyList<Skill> search) : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;

        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult(search);
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

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
