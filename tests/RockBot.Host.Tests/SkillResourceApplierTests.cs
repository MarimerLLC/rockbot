using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public class SkillResourceApplierTests
{
    [TestMethod]
    public async Task Apply_Attach_AddsProvisionalResource()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            {
              "skill": "calendar/scan",
              "filename": "fanout.json",
              "op": "attach",
              "type": "Wisp",
              "description": "Per-account fan-out",
              "content": "{\"description\":\"x\",\"steps\":[]}",
              "verifyHint": "exercises both accounts"
            }
            """);

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);

        var saved = await store.GetAsync("calendar/scan");
        var entry = saved!.Manifest!.Single();
        Assert.AreEqual("fanout.json", entry.Filename);
        Assert.IsTrue(entry.Provisional, "Self-repair attaches always land provisional");
        Assert.AreEqual("exercises both accounts", entry.VerifyHint);

        var diff = outcome.AppliedDiff;
        Assert.AreEqual("attach", diff.GetProperty("op").GetString());
        Assert.IsFalse(diff.GetProperty("replacedExisting").GetBoolean());
        Assert.IsNotNull(outcome.Revert);
    }

    [TestMethod]
    public async Task Apply_Attach_RevertRemovesNewlyAttachedResource()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var outcome = await applier.ApplyAsync(NewTicket("""
            {
              "skill": "calendar/scan",
              "filename": "fanout.json",
              "op": "attach",
              "type": "Wisp",
              "description": "x",
              "content": "{\"v\":1}"
            }
            """), CancellationToken.None);

        Assert.IsNotNull(outcome.Revert);
        await outcome.Revert!(CancellationToken.None);

        var saved = await store.GetAsync("calendar/scan");
        Assert.IsTrue(saved!.Manifest is null || saved.Manifest.Count == 0);
        var body = await store.GetResourceAsync("calendar/scan", "fanout.json");
        Assert.IsNull(body);
    }

    [TestMethod]
    public async Task Apply_Attach_RevertRestoresPriorBodyAndMetadata()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        // Pre-existing entry (validated)
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("fanout.json", SkillResourceType.Wisp, "validated", "{\"v\":1}"),
            new SkillResource(
                "fanout.json", SkillResourceType.Wisp, "validated",
                Provisional: false,
                CreatedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                VerifyHint: "validated-hint",
                DefinitionHash: "old-hash-1234567"));

        var applier = NewApplier(store);
        var outcome = await applier.ApplyAsync(NewTicket("""
            {
              "skill": "calendar/scan",
              "filename": "fanout.json",
              "op": "attach",
              "type": "Wisp",
              "description": "self-repair-attempt",
              "content": "{\"v\":2}"
            }
            """), CancellationToken.None);

        // After apply: provisional, content v2
        var afterApply = await store.GetAsync("calendar/scan");
        Assert.IsTrue(afterApply!.Manifest!.Single().Provisional);
        Assert.AreEqual("{\"v\":2}", await store.GetResourceAsync("calendar/scan", "fanout.json"));

        await outcome.Revert!(CancellationToken.None);

        // After revert: validated again, original body restored
        var afterRevert = await store.GetAsync("calendar/scan");
        var entry = afterRevert!.Manifest!.Single();
        Assert.IsFalse(entry.Provisional);
        Assert.AreEqual("validated", entry.Description);
        Assert.AreEqual("validated-hint", entry.VerifyHint);
        Assert.AreEqual("old-hash-1234567", entry.DefinitionHash);
        Assert.AreEqual("{\"v\":1}", await store.GetResourceAsync("calendar/scan", "fanout.json"));
    }

    [TestMethod]
    public async Task Apply_Delete_RemovesAndRevertRestores()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "first", "{\"v\":1}",
                Provisional: false,
                VerifyHint: "h"));

        var applier = NewApplier(store);
        var outcome = await applier.ApplyAsync(NewTicket("""
            { "skill": "calendar/scan", "filename": "a.json", "op": "delete" }
            """), CancellationToken.None);

        var afterApply = await store.GetAsync("calendar/scan");
        Assert.IsTrue(afterApply!.Manifest is null || afterApply.Manifest.Count == 0);
        Assert.IsNull(await store.GetResourceAsync("calendar/scan", "a.json"));

        await outcome.Revert!(CancellationToken.None);

        var afterRevert = await store.GetAsync("calendar/scan");
        var entry = afterRevert!.Manifest!.Single();
        Assert.AreEqual("a.json", entry.Filename);
        Assert.AreEqual("first", entry.Description);
        Assert.AreEqual("h", entry.VerifyHint);
        Assert.AreEqual("{\"v\":1}", await store.GetResourceAsync("calendar/scan", "a.json"));
    }

    [TestMethod]
    public async Task Apply_DemoteProvisional_FlipsProvisionalTrueAndRevertRestoresFalse()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{}"),
            new SkillResource("a.json", SkillResourceType.Wisp, "x",
                Provisional: false,
                CreatedAt: DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var outcome = await applier.ApplyAsync(NewTicket("""
            { "skill": "calendar/scan", "filename": "a.json", "op": "demote-provisional" }
            """), CancellationToken.None);

        Assert.IsTrue((await store.GetAsync("calendar/scan"))!.Manifest!.Single().Provisional);
        await outcome.Revert!(CancellationToken.None);
        Assert.IsFalse((await store.GetAsync("calendar/scan"))!.Manifest!.Single().Provisional);
    }

    [TestMethod]
    public async Task Apply_DemoteProvisional_AlreadyProvisional_NoOp()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{}",
                Provisional: true));

        var applier = NewApplier(store);
        var outcome = await applier.ApplyAsync(NewTicket("""
            { "skill": "calendar/scan", "filename": "a.json", "op": "demote-provisional" }
            """), CancellationToken.None);

        Assert.IsFalse(outcome.AppliedDiff.GetProperty("changed").GetBoolean());
        Assert.IsNull(outcome.Revert);
    }

    [TestMethod]
    public async Task Apply_UnknownOp_Throws()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await applier.ApplyAsync(NewTicket("""
                { "skill": "calendar/scan", "filename": "a.json", "op": "purge" }
                """), CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_AttachToMissingSkill_Throws()
    {
        var store = new InMemorySkillResourceStore();
        var applier = NewApplier(store);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await applier.ApplyAsync(NewTicket("""
                {
                  "skill": "missing",
                  "filename": "a.json",
                  "op": "attach",
                  "type": "Wisp",
                  "description": "x",
                  "content": "{}"
                }
                """), CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_DeleteMissingResource_Throws()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await applier.ApplyAsync(NewTicket("""
                { "skill": "calendar/scan", "filename": "missing.json", "op": "delete" }
                """), CancellationToken.None));
    }

    [TestMethod]
    public void Target_IsSkillResource()
    {
        var applier = NewApplier(new InMemorySkillResourceStore());
        Assert.AreEqual(RepairTarget.SkillResource, applier.Target);
    }

    private static SkillResourceApplier NewApplier(ISkillStore store) =>
        new(store, NullLogger<SkillResourceApplier>.Instance);

    private static RepairTicket NewTicket(string changeJson)
    {
        var change = JsonDocument.Parse(changeJson).RootElement;
        return new RepairTicket(
            Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.SkillResource,
            Change: change,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// In-memory <see cref="ISkillStore"/> with full Phase-2/4/5 support
/// (Attach + Remove + UpdateMetadata) — sufficient for SkillResourceApplier
/// and Phase 5 validation-pass tests.
/// </summary>
internal sealed class InMemorySkillResourceStore : ISkillStore
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(Skill skill)
    {
        _skills[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources)
    {
        _skills[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public Task<Skill?> GetAsync(string name) =>
        Task.FromResult(_skills.GetValueOrDefault(name));

    public Task<IReadOnlyList<Skill>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());

    public Task DeleteAsync(string name)
    {
        _skills.Remove(name);
        _resources.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default,
        float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);

    public Task<string?> GetResourceAsync(string skillName, string filename)
    {
        if (_resources.TryGetValue(skillName, out var files) && files.TryGetValue(filename, out var content))
            return Task.FromResult<string?>(content);
        return Task.FromResult<string?>(null);
    }

    public Task<bool> AttachResourceAsync(
        string skillName, SkillResourceInput resource, SkillResource? manifestEntry = null)
    {
        if (!_skills.TryGetValue(skillName, out var existing))
            return Task.FromResult(false);

        if (!_resources.TryGetValue(skillName, out var files))
            _resources[skillName] = files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        files[resource.Filename] = resource.Content;

        var entry = manifestEntry ?? new SkillResource(
            resource.Filename, resource.Type, resource.Description,
            Provisional: resource.Provisional,
            CreatedAt: DateTimeOffset.UtcNow,
            VerifyHint: resource.VerifyHint);

        var oldManifest = existing.Manifest ?? [];
        var newManifest = new List<SkillResource>(oldManifest.Count + 1);
        var replaced = false;
        foreach (var old in oldManifest)
        {
            if (string.Equals(old.Filename, resource.Filename, StringComparison.OrdinalIgnoreCase))
            {
                newManifest.Add(entry);
                replaced = true;
            }
            else
            {
                newManifest.Add(old);
            }
        }
        if (!replaced)
            newManifest.Add(entry);

        _skills[skillName] = existing with { Manifest = newManifest };
        return Task.FromResult(true);
    }

    public Task<bool> RemoveResourceAsync(string skillName, string filename)
    {
        if (!_skills.TryGetValue(skillName, out var existing) || existing.Manifest is null)
            return Task.FromResult(false);

        var newManifest = existing.Manifest
            .Where(r => !string.Equals(r.Filename, filename, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (newManifest.Count == existing.Manifest.Count)
            return Task.FromResult(false);

        if (_resources.TryGetValue(skillName, out var files))
            files.Remove(filename);

        _skills[skillName] = existing with { Manifest = newManifest.Count == 0 ? null : newManifest };
        return Task.FromResult(true);
    }

    public Task<bool> UpdateResourceMetadataAsync(string skillName, SkillResource updated)
    {
        if (!_skills.TryGetValue(skillName, out var existing) || existing.Manifest is null)
            return Task.FromResult(false);

        var newManifest = new List<SkillResource>(existing.Manifest.Count);
        var matched = false;
        foreach (var old in existing.Manifest)
        {
            if (string.Equals(old.Filename, updated.Filename, StringComparison.OrdinalIgnoreCase))
            {
                newManifest.Add(updated);
                matched = true;
            }
            else
            {
                newManifest.Add(old);
            }
        }
        if (!matched)
            return Task.FromResult(false);
        _skills[skillName] = existing with { Manifest = newManifest };
        return Task.FromResult(true);
    }
}
