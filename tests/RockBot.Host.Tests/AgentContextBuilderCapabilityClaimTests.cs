using System.Text.Json;
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
/// Phase 2 read-side falsification tests for <see cref="AgentContextBuilder"/>.
/// Stubs the verifier so each outcome (PredicateSucceeded, PredicateFailed, Uncertain)
/// can be exercised deterministically.
/// </summary>
[TestClass]
public class AgentContextBuilderCapabilityClaimTests
{
    [TestMethod]
    public async Task BuildAsync_PredicateSucceeded_EvictsClaimAndSkipsInjection()
    {
        var claim = ClaimEntry("claim-a", "wrapper cannot pass arguments");
        var ltm = new RecordingMemory([claim]);
        var verifier = new StubVerifier(VerifyOutcome.PredicateSucceeded);
        var builder = NewBuilder(ltm, verifier);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(1, ltm.DeletedIds.Count, "Falsified claim must be evicted from LTM.");
        Assert.AreEqual("claim-a", ltm.DeletedIds[0]);
        Assert.IsFalse(messages.Any(m => m.Text?.Contains("claim-a") == true),
            "Evicted claim must not appear in any injected context.");
    }

    [TestMethod]
    public async Task BuildAsync_PredicateFailed_InjectsClaimWithoutAnnotation()
    {
        var claim = ClaimEntry("claim-b", "wrapper cannot pass arguments");
        var ltm = new RecordingMemory([claim]);
        var verifier = new StubVerifier(VerifyOutcome.PredicateFailed);
        var builder = NewBuilder(ltm, verifier);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(0, ltm.DeletedIds.Count, "Predicate-failed claim must NOT be evicted.");
        var injected = messages.FirstOrDefault(m => m.Text?.Contains("claim-b") == true);
        Assert.IsNotNull(injected, "Predicate-failed claim must be injected as before.");
        Assert.IsFalse(injected!.Text!.Contains("verifier-uncertain"),
            "Predicate-failed claim must not carry an uncertainty annotation.");
    }

    [TestMethod]
    public async Task BuildAsync_Uncertain_InjectsClaimWithAnnotation()
    {
        var claim = ClaimEntry("claim-c", "wrapper cannot pass arguments");
        var ltm = new RecordingMemory([claim]);
        var verifier = new StubVerifier(VerifyOutcome.Uncertain, detail: "budget exceeded");
        var builder = NewBuilder(ltm, verifier);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(0, ltm.DeletedIds.Count, "Uncertain claim must NOT be evicted.");
        var injected = messages.FirstOrDefault(m => m.Text?.Contains("claim-c") == true);
        Assert.IsNotNull(injected, "Uncertain claim must be injected.");
        StringAssert.Contains(injected!.Text!, "verifier-uncertain");
        StringAssert.Contains(injected!.Text!, "budget exceeded");
    }

    [TestMethod]
    public async Task BuildAsync_NonClaimEntries_PassThroughUnchanged()
    {
        var regular = new MemoryEntry("regular-1", "user prefers concise updates", "user-preferences", [], DateTimeOffset.UtcNow);
        var ltm = new RecordingMemory([regular]);
        var verifier = new StubVerifier(VerifyOutcome.PredicateSucceeded); // would evict if it ran
        var builder = NewBuilder(ltm, verifier);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(0, ltm.DeletedIds.Count, "Non-claim entries must not be evicted.");
        Assert.AreEqual(0, verifier.CallCount, "Verifier must not be invoked for non-claim entries.");
        Assert.IsTrue(messages.Any(m => m.Text?.Contains("regular-1") == true),
            "Regular entry must be injected as before.");
    }

    [TestMethod]
    public async Task BuildAsync_NoVerifierConfigured_ClaimsPassThrough_BackwardCompat()
    {
        var claim = ClaimEntry("claim-d", "wrapper cannot pass arguments");
        var ltm = new RecordingMemory([claim]);
        var builder = NewBuilder(ltm, verifier: null);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(0, ltm.DeletedIds.Count);
        Assert.IsTrue(messages.Any(m => m.Text?.Contains("claim-d") == true),
            "Without a verifier, claim entries fall through to the existing injection path.");
    }

    [TestMethod]
    public async Task BuildAsync_VerifierThrows_InjectsWithUncertaintyAnnotation()
    {
        var claim = ClaimEntry("claim-e", "wrapper cannot pass arguments");
        var ltm = new RecordingMemory([claim]);
        var verifier = new StubVerifier(VerifyOutcome.PredicateSucceeded)
        {
            ThrowOnNext = new InvalidOperationException("bridge unavailable")
        };
        var builder = NewBuilder(ltm, verifier);

        var messages = await builder.BuildAsync("session-1", "anything", CancellationToken.None);

        Assert.AreEqual(0, ltm.DeletedIds.Count, "Verifier exceptions must not cause eviction.");
        var injected = messages.FirstOrDefault(m => m.Text?.Contains("claim-e") == true);
        Assert.IsNotNull(injected);
        StringAssert.Contains(injected!.Text!, "verifier-error");
    }

    // --- helpers -------------------------------------------------------------

    private static MemoryEntry ClaimEntry(string id, string statement) =>
        new(id, statement,
            CapabilityClaimCategories.For("calendar-mcp", "get_calendar_events"),
            ["capability-claim"],
            DateTimeOffset.UtcNow)
        {
            Verify = new VerifyShape(
                "calendar-mcp", "get_calendar_events",
                JsonDocument.Parse("""{"timeZone":"America/Chicago"}""").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success))
        };

    private static AgentContextBuilder NewBuilder(ILongTermMemory ltm, StubVerifier? verifier)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "Test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));

        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-claim-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            profileOpts,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, new AgentNameHolder()),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new StubConversationMemory(),
            longTermMemory: ltm,
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
            capabilityClaimVerifier: verifier);
    }

    /// <summary>
    /// In-test <see cref="ICapabilityClaimVerifier"/> with deterministic outcomes.
    /// Lets us exercise each branch of the AgentContextBuilder filter without spinning
    /// up the real MCP gateway path.
    /// </summary>
    private sealed class StubVerifier : ICapabilityClaimVerifier
    {
        public StubVerifier(VerifyOutcome outcome, string? detail = null)
        {
            Outcome = outcome;
            Detail = detail;
        }
        public VerifyOutcome Outcome { get; }
        public string? Detail { get; }
        public Exception? ThrowOnNext { get; set; }
        public int CallCount { get; set; }

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken ct = default)
        {
            CallCount++;
            if (ThrowOnNext is { } ex)
            {
                ThrowOnNext = null;
                throw ex;
            }
            return Task.FromResult(new VerifyResult(Outcome, Detail));
        }
    }

    private sealed class RecordingMemory : ILongTermMemory
    {
        private readonly List<MemoryEntry> _entries;
        public List<string> DeletedIds { get; } = new();

        public RecordingMemory(IReadOnlyList<MemoryEntry> seed) => _entries = [.. seed];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            // Return everything for the unbiased recall path; nothing for category-scoped queries
            // (episodic / identity) so we focus assertions on the LTM seam.
            if (criteria.Category is not null)
                return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(id);
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

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
