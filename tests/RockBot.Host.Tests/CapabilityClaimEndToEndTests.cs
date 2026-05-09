using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// End-to-end Phase 2 acceptance test: a capability claim written via
/// <see cref="ICapabilityClaimWriter"/> into a real <see cref="FileMemoryStore"/>
/// is evicted from disk when the read-side verifier reports
/// <see cref="VerifyOutcome.PredicateSucceeded"/>, and the next session sees no entry.
/// </summary>
[TestClass]
public class CapabilityClaimEndToEndTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-claim-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task ClaimSurvivesRoundTrip_AndIsEvictedWhenPredicateSucceeds()
    {
        // 1. Real file-backed long-term memory.
        var memoryOpts = Options.Create(new MemoryOptions { BasePath = Path.Combine(_tempDir, "ltm") });
        var profileOpts = Options.Create(new AgentProfileOptions { BasePath = Path.Combine(_tempDir, "profile") });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var embedOpts = Options.Create(new EmbeddingOptions());

        var ltm = new FileMemoryStore(
            memoryOpts, profileOpts, embedOpts,
            NullLogger<FileMemoryStore>.Instance);

        // 2. Save a claim through the real writer.
        var writer = new CapabilityClaimWriter(ltm);
        var claim = new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper cannot pass arguments to get_calendar_events",
            Verify: new VerifyShape(
                Server: "calendar-mcp",
                Tool: "get_calendar_events",
                Arguments: JsonDocument.Parse("""{"accountId":"x","timeZone":"America/Chicago","startDate":"2026-05-08","endDate":"2026-05-08"}""").RootElement,
                Expect: new VerifyExpectation(VerifyExpectationKind.Success)),
            Evidence: ["recovery exhausted"],
            CreatedAt: DateTimeOffset.UtcNow);
        await writer.SaveCapabilityClaimAsync(claim);

        // 3. Confirm the entry round-trips with Verify populated.
        var allCategories = await ltm.ListCategoriesAsync();
        Assert.IsTrue(allCategories.Any(c => c.StartsWith("claim/capability/calendar-mcp")),
            "Claim category did not appear after save.");

        var search = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 10));
        Assert.AreEqual(1, search.Count, "Expected exactly one claim entry on disk.");
        Assert.IsNotNull(search[0].Verify, "Verify shape did not survive round-trip serialisation.");
        Assert.AreEqual("calendar-mcp", search[0].Verify!.Server);

        // 4. Run the read-side filter — verifier reports the claim is falsified.
        var verifier = new AlwaysSucceedingVerifier();
        var builder = NewBuilder(ltm, verifier, profileOpts);

        // Use a query that BM25 will match against the claim content so the recall path
        // surfaces the entry; otherwise the entry wouldn't reach the read-side filter.
        var messages = await builder.BuildAsync("session-1", "wrapper cannot pass arguments", CancellationToken.None);

        // 5. Claim must be evicted from disk and absent from the chat context.
        var afterSearch = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 10));
        Assert.AreEqual(0, afterSearch.Count, "Falsified claim was not evicted from long-term memory.");
        Assert.IsFalse(messages.Any(m => m.Text?.Contains("wrapper cannot pass arguments") == true),
            "Falsified claim leaked into injected context.");

        Assert.AreEqual(1, verifier.CallCount, "Verifier was not invoked exactly once for the claim.");
    }

    [TestMethod]
    public async Task ClaimWithFailingVerify_IsRetainedAndInjected()
    {
        // Claim survives because the verifier reports the predicate failed
        // (the call returned an error matching the asserted limitation).
        var memoryOpts = Options.Create(new MemoryOptions { BasePath = Path.Combine(_tempDir, "ltm") });
        var profileOpts = Options.Create(new AgentProfileOptions { BasePath = Path.Combine(_tempDir, "profile") });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var embedOpts = Options.Create(new EmbeddingOptions());

        var ltm = new FileMemoryStore(
            memoryOpts, profileOpts, embedOpts,
            NullLogger<FileMemoryStore>.Instance);

        var writer = new CapabilityClaimWriter(ltm);
        await writer.SaveCapabilityClaimAsync(new CapabilityClaim(
            Server: "calendar-mcp",
            Tool: "get_calendar_events",
            Statement: "wrapper cannot pass arguments to get_calendar_events",
            Verify: new VerifyShape(
                "calendar-mcp", "get_calendar_events",
                JsonDocument.Parse("""{"accountId":"x"}""").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Evidence: [],
            CreatedAt: DateTimeOffset.UtcNow));

        var verifier = new AlwaysFailingVerifier();
        var builder = NewBuilder(ltm, verifier, profileOpts);

        await builder.BuildAsync("session-1", "wrapper cannot pass arguments", CancellationToken.None);

        // Claim is preserved on disk.
        var search = await ltm.SearchAsync(new MemorySearchCriteria(
            Category: CapabilityClaimCategories.Prefix, MaxResults: 10));
        Assert.AreEqual(1, search.Count, "Predicate-failed claim must remain on disk.");
    }

    private static AgentContextBuilder NewBuilder(
        ILongTermMemory ltm, ICapabilityClaimVerifier verifier, IOptions<AgentProfileOptions> profileOpts)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "Test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            profileOpts,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, new AgentNameHolder(), Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions())),
            rulesStore: new EmptyRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new EmptyConversationMemory(),
            longTermMemory: ltm,
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new EmptyWorkingMemory(),
            skillStore: new EmptySkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance,
            capabilityClaimVerifier: verifier);
    }

    private sealed class AlwaysSucceedingVerifier : ICapabilityClaimVerifier
    {
        public int CallCount { get; private set; }
        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new VerifyResult(VerifyOutcome.PredicateSucceeded));
        }
    }

    private sealed class AlwaysFailingVerifier : ICapabilityClaimVerifier
    {
        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken ct = default) =>
            Task.FromResult(new VerifyResult(VerifyOutcome.PredicateFailed, "expected"));
    }

    private sealed class EmptyConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class EmptyWorkingMemory : IWorkingMemory
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

    private sealed class EmptySkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class EmptyRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}
