using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

[TestClass]
public class AgentIdentityTests
{
    // ── AgentIdentityCategories constants ────────────────────────────────────

    [TestMethod]
    public void Prefix_IsAgentIdentity()
    {
        Assert.AreEqual("agent-identity", AgentIdentityCategories.Prefix);
    }

    [TestMethod]
    public void AllCategories_StartWithPrefix()
    {
        var categories = new[]
        {
            AgentIdentityCategories.Mission,
            AgentIdentityCategories.Goals,
            AgentIdentityCategories.Projects,
            AgentIdentityCategories.Capabilities,
            AgentIdentityCategories.SelfModel
        };

        foreach (var cat in categories)
            Assert.IsTrue(cat.StartsWith(AgentIdentityCategories.Prefix + "/"),
                $"Category '{cat}' should start with '{AgentIdentityCategories.Prefix}/'");
    }

    [TestMethod]
    public void SubCategories_HaveExpectedValues()
    {
        Assert.AreEqual("agent-identity/mission", AgentIdentityCategories.Mission);
        Assert.AreEqual("agent-identity/goals", AgentIdentityCategories.Goals);
        Assert.AreEqual("agent-identity/projects", AgentIdentityCategories.Projects);
        Assert.AreEqual("agent-identity/capabilities", AgentIdentityCategories.Capabilities);
        Assert.AreEqual("agent-identity/self-model", AgentIdentityCategories.SelfModel);
    }

    // ── DreamOptions identity defaults ──────────────────────────────────────

    [TestMethod]
    public void DreamOptions_IdentityReflectionEnabled_DefaultsToTrue()
    {
        var options = new DreamOptions();
        Assert.IsTrue(options.IdentityReflectionEnabled);
    }

    [TestMethod]
    public void DreamOptions_IdentityDirectivePath_DefaultsToIdentityDreamMd()
    {
        var options = new DreamOptions();
        Assert.AreEqual("identity-dream.md", options.IdentityDirectivePath);
    }

    // ── Identity DTO serialization ──────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [TestMethod]
    public void IdentityReflectionResult_Deserializes_WithEntries()
    {
        var json = """
        {
          "noChange": false,
          "toDelete": ["abc123"],
          "toSave": [
            {
              "content": "I have become primarily a communication manager.",
              "category": "agent-identity/self-model",
              "tags": ["identity"],
              "importance": 0.8
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<IdentityReflectionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.NoChange);
        Assert.AreEqual(1, result.ToDelete?.Count);
        Assert.AreEqual("abc123", result.ToDelete![0]);
        Assert.AreEqual(1, result.ToSave?.Count);

        var entry = result.ToSave![0];
        Assert.AreEqual("I have become primarily a communication manager.", entry.Content);
        Assert.AreEqual("agent-identity/self-model", entry.Category);
        Assert.AreEqual(0.8f, entry.Importance);
    }

    [TestMethod]
    public void IdentityReflectionResult_Deserializes_NoChange()
    {
        var json = """{"noChange": true, "toDelete": [], "toSave": []}""";

        var result = JsonSerializer.Deserialize<IdentityReflectionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.NoChange);
        Assert.AreEqual(0, result.ToSave?.Count);
        Assert.AreEqual(0, result.ToDelete?.Count);
    }

    // ── AgentContextBuilder: identity injection ─────────────────────────────

    [TestMethod]
    public async Task BuildAsync_InjectsIdentityEntries_WithFirstPersonFraming_ForPrimaryAgent()
    {
        var identityEntry = new MemoryEntry(
            Id: "id001",
            Content: "I primarily manage schedules and communications.",
            Category: AgentIdentityCategories.SelfModel,
            Tags: ["identity"],
            CreatedAt: DateTimeOffset.UtcNow);

        var memory = new FakeLongTermMemory(identityEntry);
        var builder = CreateContextBuilder(memory);

        var messages = await builder.BuildAsync("test-session", "hello", CancellationToken.None);

        var identityMessage = messages.FirstOrDefault(m =>
            m.Role == ChatRole.System &&
            m.Text?.Contains("Your evolving identity") == true);

        Assert.IsNotNull(identityMessage, "Should contain identity context for primary agent");
        Assert.IsTrue(identityMessage.Text!.Contains("I primarily manage schedules"),
            "Should contain the identity entry content");
        Assert.IsFalse(identityMessage.Text!.Contains("subordinate agent"),
            "Primary agent should not see subordinate framing");
    }

    [TestMethod]
    public async Task BuildAsync_InjectsIdentityEntries_WithThirdPersonFraming_ForSubagent()
    {
        var identityEntry = new MemoryEntry(
            Id: "id002",
            Content: "I primarily manage schedules and communications.",
            Category: AgentIdentityCategories.SelfModel,
            Tags: ["identity"],
            CreatedAt: DateTimeOffset.UtcNow);

        var memory = new FakeLongTermMemory(identityEntry);
        var builder = CreateContextBuilder(memory);

        // systemPromptOverride non-null signals subagent/patrol context
        var messages = await builder.BuildAsync(
            "subagent/task-123", "do research", CancellationToken.None,
            workingMemoryNamespace: "subagent/task-123",
            systemPromptOverride: "You are a subagent.");

        var identityMessage = messages.FirstOrDefault(m =>
            m.Role == ChatRole.System &&
            m.Text?.Contains("Primary agent identity context") == true);

        Assert.IsNotNull(identityMessage, "Should contain identity context for subagent");
        Assert.IsTrue(identityMessage.Text!.Contains("subordinate agent"),
            "Subagent should see subordinate framing");
        Assert.IsTrue(identityMessage.Text!.Contains("I primarily manage schedules"),
            "Should contain the identity entry content");
    }

    [TestMethod]
    public async Task BuildAsync_SkipsIdentityInjection_WhenNoEntries()
    {
        var memory = new FakeLongTermMemory(); // no identity entries
        var builder = CreateContextBuilder(memory);

        var messages = await builder.BuildAsync("test-session", "hello", CancellationToken.None);

        var identityMessage = messages.FirstOrDefault(m =>
            m.Role == ChatRole.System &&
            (m.Text?.Contains("evolving identity") == true ||
             m.Text?.Contains("Primary agent identity") == true));

        Assert.IsNull(identityMessage, "Should not inject identity block when no entries exist");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentContextBuilder CreateContextBuilder(ILongTermMemory memory)
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul content.");
        var directives = new AgentProfileDocument("directives", null, [], "Directive content.");
        var profile = new AgentProfile(soul, directives);
        var holder = new ProfileHolder();
        holder.Update(profile);

        return new AgentContextBuilder(
            profileHolder: holder,
            agent: new AgentIdentity("test-agent"),
            promptBuilder: new FakeSystemPromptBuilder(),
            rulesStore: new FakeRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new FakeConversationMemory(),
            longTermMemory: memory,
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new FakeWorkingMemory(),
            skillStore: new FakeSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake long-term memory that returns identity entries when queried with the
    /// agent-identity category prefix, and empty results otherwise.
    /// </summary>
    private sealed class FakeLongTermMemory(params MemoryEntry[] identityEntries) : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            if (criteria.Category is not null &&
                criteria.Category.StartsWith(AgentIdentityCategories.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IReadOnlyList<MemoryEntry>>(identityEntries.ToList());
            }
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeSystemPromptBuilder : ISystemPromptBuilder
    {
        public string Build(AgentProfile profile, AgentIdentity identity) => "System prompt.";
    }

    private sealed class FakeRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }

    private sealed class FakeConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
            => Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null)
            => Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class FakeSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null)
            => Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    // ── DTOs (mirrors DreamService private records for deserialization testing) ──

    internal sealed record IdentityReflectionResultDto(
        bool? NoChange,
        List<string>? ToDelete,
        List<IdentityEntryDto>? ToSave);

    internal sealed record IdentityEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        float? Importance);
}
