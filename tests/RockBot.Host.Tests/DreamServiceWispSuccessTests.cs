using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Phase 3 (skill-asset promotion): exercise the apply loop of the success-shaped
/// dream pass directly so we don't need to drive a real LLM. The pass's outer
/// shape — query log → group by hash → resolve invokingSkill → call LLM → apply —
/// is unit-tested at the apply boundary; the LLM-driven half is covered by the
/// directive prompt itself.
/// </summary>
[TestClass]
public class DreamServiceWispSuccessTests
{
    [TestMethod]
    public async Task ApplyPromotions_AttachesNonProvisionalResourceToTargetSkill()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var body = """{"description":"x","steps":[]}""";
        var candidate = new DreamService.WispSuccessCandidate(
            DefinitionHash: "h-abc",
            Frequency: 4,
            DistinctSessions: 3,
            Description: "Per-account fan-out",
            InvokingSkill: "calendar/scan",
            Body: body);

        var promotion = new DreamService.WispSuccessPromotionDto
        {
            TargetSkill = "calendar/scan",
            Filename = "fanout.json",
            ResourceType = SkillResourceType.Wisp,
            Description = "fan-out asset",
            DefinitionHash = "h-abc"
        };

        var attached = await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calendar/scan" },
            [promotion], NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(1, attached);

        var saved = await store.GetAsync("calendar/scan");
        var entry = saved!.Manifest!.Single();
        Assert.AreEqual("fanout.json", entry.Filename);
        Assert.AreEqual(SkillResourceType.Wisp, entry.Type);
        Assert.IsFalse(entry.Provisional, "Dream-pass promotions land non-provisional");
        Assert.AreEqual("h-abc", entry.DefinitionHash);
        Assert.IsNotNull(entry.CreatedAt);

        // Body persisted verbatim from candidate
        var written = await store.GetResourceAsync("calendar/scan", "fanout.json");
        Assert.AreEqual(body, written);
    }

    [TestMethod]
    public async Task ApplyPromotions_SkipsTargetSkillNotInExistingSet()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var candidate = new DreamService.WispSuccessCandidate(
            "h-abc", 3, 2, "x", "calendar/scan", "{}");

        var promotion = new DreamService.WispSuccessPromotionDto
        {
            TargetSkill = "non-existent-skill",
            Filename = "x.json",
            ResourceType = SkillResourceType.Wisp,
            Description = "x",
            DefinitionHash = "h-abc"
        };

        var attached = await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calendar/scan" },
            [promotion], NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, attached);
    }

    [TestMethod]
    public async Task ApplyPromotions_SkipsPromotionForUnknownHash()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var candidate = new DreamService.WispSuccessCandidate(
            "h-abc", 3, 2, "x", "calendar/scan", "{}");

        var promotion = new DreamService.WispSuccessPromotionDto
        {
            TargetSkill = "calendar/scan",
            Filename = "x.json",
            ResourceType = SkillResourceType.Wisp,
            Description = "x",
            DefinitionHash = "h-totally-different"
        };

        var attached = await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calendar/scan" },
            [promotion], NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, attached);
    }

    [TestMethod]
    public async Task ApplyPromotions_FillsDescriptionFromCandidateWhenLlmOmits()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var candidate = new DreamService.WispSuccessCandidate(
            "h-abc", 3, 2, "candidate-description", "calendar/scan", "{}");

        var promotion = new DreamService.WispSuccessPromotionDto
        {
            TargetSkill = "calendar/scan",
            Filename = "x.json",
            ResourceType = SkillResourceType.Wisp,
            Description = null,  // LLM forgot to fill
            DefinitionHash = "h-abc"
        };

        await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calendar/scan" },
            [promotion], NullLogger.Instance, CancellationToken.None);

        var saved = await store.GetAsync("calendar/scan");
        Assert.AreEqual("candidate-description", saved!.Manifest!.Single().Description);
    }

    [TestMethod]
    public async Task ApplyPromotions_NullPromotions_ReturnsZero()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        var attached = await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [], [], null, NullLogger.Instance, CancellationToken.None);
        Assert.AreEqual(0, attached);
    }

    [TestMethod]
    public async Task ApplyPromotions_EmptyOrMalformed_AreSkipped()
    {
        var store = new InMemorySkillStoreForSuccessTests();
        await store.SaveAsync(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var candidate = new DreamService.WispSuccessCandidate(
            "h-abc", 3, 2, "x", "calendar/scan", "{}");

        // Various malformed promotions
        var promotions = new[]
        {
            new DreamService.WispSuccessPromotionDto { TargetSkill = "", Filename = "x", DefinitionHash = "h-abc" },
            new DreamService.WispSuccessPromotionDto { TargetSkill = "calendar/scan", Filename = "", DefinitionHash = "h-abc" },
            new DreamService.WispSuccessPromotionDto { TargetSkill = "calendar/scan", Filename = "x", DefinitionHash = "" },
        };

        var attached = await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "calendar/scan" },
            promotions, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, attached);
    }
}

/// <summary>
/// Minimal in-memory <see cref="ISkillStore"/> sufficient to exercise the
/// success-pass apply loop. Mirrors <see cref="FileSkillStore.AttachResourceAsync"/>
/// semantics for the test surface only.
/// </summary>
internal sealed class InMemorySkillStoreForSuccessTests : ISkillStore
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
}
