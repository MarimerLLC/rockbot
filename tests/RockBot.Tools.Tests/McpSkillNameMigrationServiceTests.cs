using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

/// <summary>
/// Verifies <see cref="McpSkillNameMigrationService.MigrateAsync"/> behavior. Drives
/// the migration entry point directly (bypassing the index-population polling path)
/// against an in-memory <see cref="ISkillStore"/>.
/// </summary>
[TestClass]
public class McpSkillNameMigrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MigrateAsync_RenamesTopLevelMatchingSkillToMcpNamespace()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("calendar-mcp", "calendar content"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            new[] { Server("calendar-mcp") },
            CancellationToken.None);

        Assert.AreEqual(1, summary.Renamed);
        Assert.AreEqual(0, summary.RemovedDuplicate);

        Assert.IsNull(await store.GetAsync("calendar-mcp"),
            "The legacy top-level entry should be deleted after rename.");

        var migrated = await store.GetAsync("mcp/calendar-mcp");
        Assert.IsNotNull(migrated);
        Assert.AreEqual("calendar content", migrated!.Content,
            "Migration must preserve content verbatim.");
        Assert.IsNotNull(migrated.UpdatedAt);
    }

    [TestMethod]
    public async Task MigrateAsync_LowercasesNewName()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("Calendar-MCP", "mixed case"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            new[] { Server("calendar-mcp") },
            CancellationToken.None);

        Assert.AreEqual(1, summary.Renamed);
        Assert.IsNotNull(await store.GetAsync("mcp/calendar-mcp"));
        Assert.IsNull(await store.GetAsync("Calendar-MCP"));
    }

    [TestMethod]
    public async Task MigrateAsync_LeavesAlreadyNamespacedSkillsUntouched()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("mcp/calendar-mcp", "already migrated"));
        // Sub-skills under the namespace are also valid (large-server pattern) and
        // must not be touched by the migration.
        await store.SaveAsync(MakeSkill("mcp/ms365/email-tools", "sub-skill"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            new[] { Server("calendar-mcp"), Server("ms365") },
            CancellationToken.None);

        Assert.AreEqual(0, summary.Renamed);
        Assert.AreEqual(0, summary.RemovedDuplicate);

        Assert.IsNotNull(await store.GetAsync("mcp/calendar-mcp"));
        Assert.IsNotNull(await store.GetAsync("mcp/ms365/email-tools"));
    }

    [TestMethod]
    public async Task MigrateAsync_DropsLegacyEntryWhenNamespacedTargetAlreadyExists()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("calendar-mcp", "stale duplicate"));
        await store.SaveAsync(MakeSkill("mcp/calendar-mcp", "canonical"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            new[] { Server("calendar-mcp") },
            CancellationToken.None);

        Assert.AreEqual(0, summary.Renamed);
        Assert.AreEqual(1, summary.RemovedDuplicate);

        Assert.IsNull(await store.GetAsync("calendar-mcp"),
            "The duplicate top-level entry should be removed.");

        var preserved = await store.GetAsync("mcp/calendar-mcp");
        Assert.IsNotNull(preserved);
        Assert.AreEqual("canonical", preserved!.Content,
            "The pre-existing namespaced entry must be preserved unchanged.");
    }

    [TestMethod]
    public async Task MigrateAsync_LeavesNonMatchingTopLevelSkillUntouched()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("plan-meeting", "unrelated topical skill"));
        await store.SaveAsync(MakeSkill("research-summary", "another unrelated skill"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            new[] { Server("calendar-mcp"), Server("ms365") },
            CancellationToken.None);

        Assert.AreEqual(0, summary.Renamed);
        Assert.AreEqual(0, summary.RemovedDuplicate);

        Assert.IsNotNull(await store.GetAsync("plan-meeting"));
        Assert.IsNotNull(await store.GetAsync("research-summary"));
    }

    [TestMethod]
    public async Task MigrateAsync_NoServers_DoesNothing()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(MakeSkill("calendar-mcp", "content"));

        var service = CreateService(store);
        var summary = await service.MigrateAsync(
            Array.Empty<McpServerSummary>(),
            CancellationToken.None);

        Assert.AreEqual(0, summary.Renamed);
        Assert.AreEqual(0, summary.RemovedDuplicate);
        Assert.IsNotNull(await store.GetAsync("calendar-mcp"));
    }

    private static McpSkillNameMigrationService CreateService(ISkillStore store)
    {
        return new McpSkillNameMigrationService(
            store,
            new McpServerIndex(),
            NullLogger<McpSkillNameMigrationService>.Instance);
    }

    private static Skill MakeSkill(string name, string content) => new(
        Name: name,
        Summary: $"summary for {name}",
        Content: content,
        CreatedAt: Now);

    private static McpServerSummary Server(string name) =>
        new() { ServerName = name };

    /// <summary>
    /// In-memory <see cref="ISkillStore"/> fake: covers the subset of the API the
    /// migration service exercises (Save / Get / List / Delete).
    /// </summary>
    private sealed class InMemorySkillStore : ISkillStore
    {
        private readonly Dictionary<string, Skill> _skills = new(StringComparer.Ordinal);

        public Task SaveAsync(Skill skill)
        {
            _skills[skill.Name] = skill;
            return Task.CompletedTask;
        }

        public Task<Skill?> GetAsync(string name)
        {
            _skills.TryGetValue(name, out var skill);
            return Task.FromResult<Skill?>(skill);
        }

        public Task<IReadOnlyList<Skill>> ListAsync()
        {
            IReadOnlyList<Skill> list = _skills.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(list);
        }

        public Task DeleteAsync(string name)
        {
            _skills.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default,
            float[]? queryEmbedding = null)
        {
            return ListAsync();
        }
    }
}
