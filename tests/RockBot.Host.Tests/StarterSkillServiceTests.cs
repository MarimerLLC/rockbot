using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Tools;

namespace RockBot.Host.Tests;

/// <summary>
/// The starter skills are code-owned (#699): startup seeds a missing one, re-syncs a stored copy
/// that differs from code (keeping its history and attachments), leaves a matching one alone,
/// and releases a flagged skill whose guide is no longer registered.
/// </summary>
[TestClass]
public class StarterSkillServiceTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastUsed = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Absent_IsSeeded_AndFlagged()
    {
        var store = new CountingSkillStore();

        await CreateService(store, new StubProvider("memory", "Memory tools", "# Memory guide"))
            .StartAsync(CancellationToken.None);

        var seeded = (await store.GetAsync("memory"))!;
        Assert.AreEqual("# Memory guide", seeded.Content);
        Assert.AreEqual("Memory tools", seeded.Summary);
        Assert.IsTrue(seeded.CodeOwned);
    }

    [TestMethod]
    public async Task EditedContent_IsRestored_KeepingHistoryAndAttachments()
    {
        var store = new CountingSkillStore();
        var manifest = new List<SkillResource> { new("fanout.json", SkillResourceType.Wisp, "eager wisp") };
        await store.SaveAsync(new Skill("memory", "Memory tools", "# Memory guide\n\n## Wisp Failure Pattern\n\nedited",
            Created, Created, LastUsed, SeeAlso: ["memory-learned"], Manifest: manifest, CodeOwned: true));

        await CreateService(store, new StubProvider("memory", "Memory tools", "# Memory guide"))
            .StartAsync(CancellationToken.None);

        var resynced = (await store.GetAsync("memory"))!;
        Assert.AreEqual("# Memory guide", resynced.Content);
        Assert.AreEqual(Created, resynced.CreatedAt);
        Assert.AreEqual(LastUsed, resynced.LastUsedAt);
        Assert.AreSame(manifest, resynced.Manifest);
        CollectionAssert.AreEqual(new[] { "memory-learned" }, resynced.SeeAlso!.ToArray());
        Assert.IsTrue(resynced.UpdatedAt > Created);
        Assert.IsTrue(resynced.CodeOwned);
    }

    [TestMethod]
    public async Task ProviderDocumentChanged_IsOverwritten()
    {
        var store = new CountingSkillStore();
        await store.SaveAsync(new Skill("mcp", "MCP tools", "# Old guide", Created, CodeOwned: true));

        await CreateService(store, new StubProvider("mcp", "MCP tools, now with forms", "# New guide"))
            .StartAsync(CancellationToken.None);

        var resynced = (await store.GetAsync("mcp"))!;
        Assert.AreEqual("# New guide", resynced.Content);
        Assert.AreEqual("MCP tools, now with forms", resynced.Summary);
    }

    [TestMethod]
    public async Task UnflaggedCopyMatchingCode_IsFlagged()
    {
        // A copy seeded before #699 matches code but lacks the flag.
        var store = new CountingSkillStore();
        await store.SaveAsync(new Skill("web", "Web tools", "# Web guide", Created));

        await CreateService(store, new StubProvider("web", "Web tools", "# Web guide"))
            .StartAsync(CancellationToken.None);

        Assert.IsTrue((await store.GetAsync("web"))!.CodeOwned);
    }

    [TestMethod]
    public async Task Unchanged_IsNotSaved()
    {
        var store = new CountingSkillStore();
        await store.SaveAsync(new Skill("memory", "Memory tools", "# Memory guide", Created, CodeOwned: true));
        store.Saves = 0;

        await CreateService(store, new StubProvider("memory", "Memory tools", "# Memory guide"))
            .StartAsync(CancellationToken.None);

        Assert.AreEqual(0, store.Saves);
    }

    [TestMethod]
    public async Task OrphanedFlaggedSkill_IsReleased()
    {
        var store = new CountingSkillStore();
        await store.SaveAsync(new Skill("web", "Web tools", "# Web guide", Created, CodeOwned: true));
        await store.SaveAsync(new Skill("plan-meeting", "s", "body", Created));

        await CreateService(store, new StubProvider("memory", "Memory tools", "# Memory guide"))
            .StartAsync(CancellationToken.None);

        var released = (await store.GetAsync("web"))!;
        Assert.IsFalse(released.CodeOwned);
        Assert.AreEqual("# Web guide", released.Content, "released, not deleted or changed");
        Assert.IsTrue((await store.GetAsync("memory"))!.CodeOwned);
        Assert.IsFalse((await store.GetAsync("plan-meeting"))!.CodeOwned);
    }

    [TestMethod]
    public async Task InjectionDisabled_DoesNothing()
    {
        var store = new CountingSkillStore();
        await store.SaveAsync(new Skill("web", "Web tools", "# Edited", Created, CodeOwned: true));
        store.Saves = 0;

        await CreateService(store, new StubProvider("memory", "Memory tools", "# Memory guide"),
                new AgentHostOptions { EnableSkillInjection = false })
            .StartAsync(CancellationToken.None);

        Assert.AreEqual(0, store.Saves);
        Assert.IsNull(await store.GetAsync("memory"));
    }

    private static StarterSkillService CreateService(
        ISkillStore store, IToolSkillProvider provider, AgentHostOptions? options = null) =>
        new([store], [provider], NullLogger<StarterSkillService>.Instance,
            Options.Create(options ?? new AgentHostOptions()));

    internal sealed class StubProvider(string name, string summary, string document) : IToolSkillProvider
    {
        public string Name => name;
        public string Summary => summary;
        public string GetDocument() => document;
    }

    private sealed class CountingSkillStore : ISkillStore
    {
        private readonly SkillBodyApplierTests.InMemorySkillStore _inner = new();
        public int Saves { get; set; }

        public Task SaveAsync(Skill skill)
        {
            Saves++;
            return _inner.SaveAsync(skill);
        }

        public Task<Skill?> GetAsync(string name) => _inner.GetAsync(name);
        public Task<IReadOnlyList<Skill>> ListAsync() => _inner.ListAsync();
        public Task DeleteAsync(string name) => _inner.DeleteAsync(name);

        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            _inner.SearchAsync(query, maxResults, cancellationToken, queryEmbedding);
    }
}
