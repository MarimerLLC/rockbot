using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Skills;

namespace RockBot.Agent.Tests;

[TestClass]
public class SkillToolsTests
{
    // ── FormatIndex ───────────────────────────────────────────────────────────

    [TestMethod]
    public void FormatIndex_EmptyList_ReturnsNoSkillsMessage()
    {
        var result = SkillTools.FormatIndex([]);
        Assert.AreEqual("No skills saved yet.", result);
    }

    [TestMethod]
    public void FormatIndex_WithSkills_ListsNamesAndSummaries()
    {
        var skills = new List<Skill>
        {
            new("plan-meeting", "Schedule meetings efficiently", "content", DateTimeOffset.UtcNow),
            new("research/summarize", "Summarize research papers", "content", DateTimeOffset.UtcNow)
        };

        var result = SkillTools.FormatIndex(skills);

        Assert.IsTrue(result.Contains("plan-meeting"));
        Assert.IsTrue(result.Contains("Schedule meetings efficiently"));
        Assert.IsTrue(result.Contains("research/summarize"));
        Assert.IsTrue(result.Contains("Summarize research papers"));
    }

    [TestMethod]
    public void FormatIndex_EmptySummary_ShowsPending()
    {
        var skills = new List<Skill>
        {
            new("new-skill", "", "content", DateTimeOffset.UtcNow)
        };

        var result = SkillTools.FormatIndex(skills);

        Assert.IsTrue(result.Contains("(summary pending)"));
    }

    // ── GetSkill ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetSkill_ExistingSkill_ReturnsContent()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("plan-meeting", "summary", "# Plan Meeting\n\nStep 1.", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkill("plan-meeting");

        Assert.AreEqual("# Plan Meeting\n\nStep 1.", result);
    }

    [TestMethod]
    public async Task GetSkill_SkillWithManifest_IncludesResourceSection()
    {
        var store = new StubSkillStore();
        var manifest = new List<SkillResource>
        {
            new("script.py", SkillResourceType.Python, "Helper script")
        };
        store.Add(new Skill("my-skill", "summary", "# My Skill", DateTimeOffset.UtcNow, Manifest: manifest));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkill("my-skill");

        Assert.IsTrue(result.Contains("# My Skill"), "Should contain skill content");
        Assert.IsTrue(result.Contains("Resources"), "Should contain resource section header");
        Assert.IsTrue(result.Contains("script.py"), "Should list resource filename");
        Assert.IsTrue(result.Contains("Python"), "Should include resource type");
        Assert.IsTrue(result.Contains("Helper script"), "Should include resource description");
        Assert.IsTrue(result.Contains("get_skill_resource"), "Should reference the resource tool");
    }

    [TestMethod]
    public async Task GetSkill_SkillWithEmptyManifest_ReturnsContentOnly()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("my-skill", "summary", "# My Skill", DateTimeOffset.UtcNow, Manifest: []));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkill("my-skill");

        Assert.AreEqual("# My Skill", result);
    }

    [TestMethod]
    public async Task GetSkill_UnknownSkill_ReturnsNotFound()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkill("nonexistent");

        Assert.IsTrue(result.Contains("not found"));
    }

    // ── EditSkill ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Tools_ExposeEditSkill()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();

        CollectionAssert.Contains(names, "EditSkill");
    }

    [TestMethod]
    public async Task EditSkill_AppliesTheEdit_AndKeepsTheSummary()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("plan-meeting", "Plans meetings", "1. Book the room.", DateTimeOffset.UtcNow));
        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);

        var result = await tools.EditSkill("plan-meeting", "1. Book the room.", "1. Book the room.\n2. Send the invite.");

        StringAssert.Contains(result, "replaced 1 occurrence");
        var skill = await store.GetAsync("plan-meeting");
        StringAssert.Contains(skill!.Content, "2. Send the invite.");
        Assert.AreEqual("Plans meetings", skill.Summary,
            "A body edit must not blank the summary the way save_skill does.");
    }

    [TestMethod]
    public async Task EditSkill_Refusal_ReachesTheModelVerbatim()
    {
        const string refusal = "oldText was not found. It must match the content exactly.";
        var store = new StubSkillStore { EditResult = ContentEditResult.Failed(refusal) };
        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);

        var result = await tools.EditSkill("plan-meeting", "missing", "replacement");

        StringAssert.Contains(result, refusal);
    }

    [TestMethod]
    public async Task EditSkill_UnknownSkill_ReturnsNotFound()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);

        var result = await tools.EditSkill("nonexistent", "a", "b");

        StringAssert.Contains(result, "not found");
    }

    // ── GetSkillResource ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetSkillResource_ExistingResource_ReturnsContent()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("my-skill", "summary", "content", DateTimeOffset.UtcNow));
        store.AddResource("my-skill", "script.py", "print('hello')");

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkillResource("my-skill", "script.py");

        Assert.AreEqual("print('hello')", result);
    }

    [TestMethod]
    public async Task GetSkillResource_UnknownSkill_ReturnsNotFound()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkillResource("nonexistent", "script.py");

        Assert.IsTrue(result.Contains("not found"));
    }

    [TestMethod]
    public async Task GetSkillResource_UnknownResource_ReturnsNotFound()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("my-skill", "summary", "content", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkillResource("my-skill", "ghost.py");

        Assert.IsTrue(result.Contains("not found"));
        Assert.IsTrue(result.Contains("get_skill"));
    }

    // ── ListSkills ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListSkills_NoSkills_ReturnsEmptyMessage()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.ListSkills();

        Assert.AreEqual("No skills saved yet.", result);
    }

    [TestMethod]
    public async Task ListSkills_WithSkills_IncludesNamesAndSummaries()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("plan-meeting", "Schedule a meeting", "content", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.ListSkills();

        Assert.IsTrue(result.Contains("plan-meeting"));
        Assert.IsTrue(result.Contains("Schedule a meeting"));
    }

    // ── SaveSkill – manifest preservation ────────────────────────────────────

    [TestMethod]
    public async Task SaveSkill_WithoutResources_PreservesExistingManifest()
    {
        var store = new StubSkillStore();
        var manifest = new List<SkillResource>
        {
            new("script.py", SkillResourceType.Python, "Helper script")
        };
        store.Add(new Skill("my-skill", "summary", "v1 content", DateTimeOffset.UtcNow, Manifest: manifest));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);

        // Re-save without supplying resources — manifest should be preserved
        await tools.SaveSkill("my-skill", "v2 content");

        var result = await store.GetAsync("my-skill");
        Assert.IsNotNull(result);
        Assert.AreEqual("v2 content", result!.Content);
        Assert.IsNotNull(result.Manifest, "Manifest should be preserved when no resources are provided");
        Assert.AreEqual(1, result.Manifest!.Count);
        Assert.AreEqual("script.py", result.Manifest[0].Filename);
    }

    [TestMethod]
    public async Task SaveSkill_WithResources_ReplacesManifest()
    {
        var store = new StubSkillStore();
        var oldManifest = new List<SkillResource>
        {
            new("old.py", SkillResourceType.Python, "Old script")
        };
        store.Add(new Skill("my-skill", "summary", "content", DateTimeOffset.UtcNow, Manifest: oldManifest));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);

        // Re-save with new resources — manifest should be replaced
        await tools.SaveSkill("my-skill", "content",
            resources: [new SkillResourceInput("new.py", SkillResourceType.Python, "New script", "# new")]);

        var result = await store.GetAsync("my-skill");
        Assert.IsNotNull(result?.Manifest);
        Assert.AreEqual(1, result!.Manifest!.Count);
        Assert.AreEqual("new.py", result.Manifest[0].Filename);
    }

    // ── DeleteSkill ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteSkill_ExistingSkill_ReturnsConfirmationAndUpdatedIndex()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("plan-meeting", "summary", "content", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.DeleteSkill("plan-meeting");

        Assert.IsTrue(result.Contains("plan-meeting"));
        Assert.IsTrue(result.Contains("deleted"));
        Assert.IsNull(await store.GetAsync("plan-meeting"));
    }

    [TestMethod]
    public async Task DeleteSkill_UnknownSkill_ReturnsNotFound()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.DeleteSkill("ghost");

        Assert.IsTrue(result.Contains("not found"));
    }

    // ── SkillIndexTracker ─────────────────────────────────────────────────────

    [TestMethod]
    public void SkillIndexTracker_FirstCall_ReturnsTrue()
    {
        var tracker = new SkillIndexTracker();
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1"));
    }

    [TestMethod]
    public void SkillIndexTracker_SubsequentCalls_ReturnFalse()
    {
        var tracker = new SkillIndexTracker();
        tracker.TryMarkAsInjected("session-1");
        Assert.IsFalse(tracker.TryMarkAsInjected("session-1"));
    }

    [TestMethod]
    public void SkillIndexTracker_Clear_AllowsReInjection()
    {
        var tracker = new SkillIndexTracker();
        tracker.TryMarkAsInjected("session-1");
        tracker.Clear("session-1");
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1"));
    }

    [TestMethod]
    public void SkillIndexTracker_DifferentSessions_AreIndependent()
    {
        var tracker = new SkillIndexTracker();
        Assert.IsTrue(tracker.TryMarkAsInjected("session-1"));
        Assert.IsTrue(tracker.TryMarkAsInjected("session-2"));
    }

    // ── SkillRecallTracker ────────────────────────────────────────────────────

    [TestMethod]
    public void SkillRecallTracker_FirstCall_ReturnsTrue()
    {
        var tracker = new SkillRecallTracker();
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
    }

    [TestMethod]
    public void SkillRecallTracker_SecondCall_ReturnsFalse()
    {
        var tracker = new SkillRecallTracker();
        tracker.TryMarkAsRecalled("session-1", "plan-meeting");
        Assert.IsFalse(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
    }

    [TestMethod]
    public void SkillRecallTracker_SameSkillDifferentSessions_BothReturnTrue()
    {
        var tracker = new SkillRecallTracker();
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-2", "plan-meeting"));
    }

    [TestMethod]
    public void SkillRecallTracker_DifferentSkillsInSameSession_AllReturnTrue()
    {
        var tracker = new SkillRecallTracker();
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "send-email"));
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "summarize-paper"));
    }

    [TestMethod]
    public void SkillRecallTracker_Clear_AllowsReRecall()
    {
        var tracker = new SkillRecallTracker();
        tracker.TryMarkAsRecalled("session-1", "plan-meeting");
        tracker.Clear("session-1");
        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
    }

    [TestMethod]
    public void SkillRecallTracker_Clear_OnlyAffectsTargetSession()
    {
        var tracker = new SkillRecallTracker();
        tracker.TryMarkAsRecalled("session-1", "plan-meeting");
        tracker.TryMarkAsRecalled("session-2", "plan-meeting");
        tracker.Clear("session-1");

        Assert.IsTrue(tracker.TryMarkAsRecalled("session-1", "plan-meeting"));
        Assert.IsFalse(tracker.TryMarkAsRecalled("session-2", "plan-meeting"));
    }

    // ── PromoteSkillAsset (Phase 2b: skill-asset promotion) ──────────────────

    [TestMethod]
    public async Task SkillTools_DefaultCtor_DoesNotIncludePromote()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.IsFalse(names.Any(n => n.Contains("promote", StringComparison.OrdinalIgnoreCase)),
            "promote_skill_asset must not be in the main-agent tool list");
    }

    [TestMethod]
    public async Task SkillTools_EnablePromote_IncludesPromoteTool()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance,
            enablePromote: true);

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.IsTrue(names.Any(n => n.Contains("promote", StringComparison.OrdinalIgnoreCase)),
            $"promote_skill_asset should be in the subagent tool list. Got: {string.Join(", ", names)}");
    }

    [TestMethod]
    public async Task PromoteSkillAsset_AttachesProvisionalManifestEntry()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance,
            enablePromote: true);

        var body = """{"description":"x","steps":[]}""";
        var result = await tools.PromoteSkillAsset(
            "calendar/scan", "fanout.json", SkillResourceType.Wisp,
            "Per-account fan-out", body,
            verifyHint: "exercises both accounts");

        Assert.IsTrue(result.Contains("attached"), result);

        var saved = await store.GetAsync("calendar/scan");
        var entry = saved!.Manifest!.Single();
        Assert.AreEqual("fanout.json", entry.Filename);
        Assert.AreEqual(SkillResourceType.Wisp, entry.Type);
        Assert.IsTrue(entry.Provisional);
        Assert.IsNotNull(entry.CreatedAt);
        Assert.AreEqual("exercises both accounts", entry.VerifyHint);
        Assert.IsNotNull(entry.DefinitionHash);
        Assert.AreEqual(16, entry.DefinitionHash!.Length);

        // DefinitionHash should match SHA-256-hex16 of the body
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        var expected = Convert.ToHexStringLower(bytes)[..16];
        Assert.AreEqual(expected, entry.DefinitionHash);
    }

    [TestMethod]
    public async Task PromoteSkillAsset_UnknownSkill_ReturnsHelpfulError()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance,
            enablePromote: true);

        var result = await tools.PromoteSkillAsset(
            "nope", "x.json", SkillResourceType.Wisp, "x", "{}");

        Assert.IsTrue(result.Contains("not found"));
        Assert.IsTrue(result.Contains("save_skill"));
    }

    [TestMethod]
    public async Task PromoteSkillAsset_TwiceSameFilename_ReplacesPreservingOthers()
    {
        var store = new StubSkillStore();
        store.Add(new Skill("calendar/scan", "Scan", "# Scan", DateTimeOffset.UtcNow));

        var tools = new SkillTools(store, new StubChatClient(), NullLogger<SkillTools>.Instance,
            enablePromote: true);

        await tools.PromoteSkillAsset("calendar/scan", "a.json", SkillResourceType.Wisp, "first", "{\"v\":1}");
        await tools.PromoteSkillAsset("calendar/scan", "b.py", SkillResourceType.Python, "second", "print('b')");
        await tools.PromoteSkillAsset("calendar/scan", "a.json", SkillResourceType.Wisp, "first-v2", "{\"v\":2}");

        var saved = await store.GetAsync("calendar/scan");
        Assert.AreEqual(2, saved!.Manifest!.Count);
        Assert.AreEqual("first-v2", saved.Manifest.Single(r => r.Filename == "a.json").Description);

        // b.py is preserved
        var bBody = await store.GetResourceAsync("calendar/scan", "b.py");
        Assert.AreEqual("print('b')", bBody);
    }

    // ── FormatResourceTag — provisional asterisk ─────────────────────────────

    [TestMethod]
    public void FormatResourceTag_ProvisionalEntry_AppendsAsterisk()
    {
        var manifest = new List<SkillResource>
        {
            new("a.json", SkillResourceType.Wisp, "x", Provisional: true),
        };
        var tag = SkillTools.FormatResourceTag(manifest);
        Assert.AreEqual(" [Wisp*]", tag);
    }

    [TestMethod]
    public void FormatResourceTag_MixedProvisionalAndValidated_OnlyMarksAffectedTypes()
    {
        var manifest = new List<SkillResource>
        {
            new("a.json", SkillResourceType.Wisp, "x", Provisional: true),
            new("script.py", SkillResourceType.Python, "y"),
            new("schema.json", SkillResourceType.JsonSchema, "z"),
        };
        var tag = SkillTools.FormatResourceTag(manifest);
        Assert.AreEqual(" [JsonSchema, Python, Wisp*]", tag);
    }

    [TestMethod]
    public void FormatResourceTag_ProvisionalAndValidatedSameType_UsesAsterisk()
    {
        var manifest = new List<SkillResource>
        {
            new("a.json", SkillResourceType.Wisp, "x", Provisional: true),
            new("b.json", SkillResourceType.Wisp, "y"),  // validated
        };
        var tag = SkillTools.FormatResourceTag(manifest);
        Assert.AreEqual(" [Wisp*]", tag);
    }

    [TestMethod]
    public void FormatResourceTag_AllValidated_NoAsterisk()
    {
        var manifest = new List<SkillResource>
        {
            new("a.json", SkillResourceType.Wisp, "x"),
            new("b.py", SkillResourceType.Python, "y"),
        };
        var tag = SkillTools.FormatResourceTag(manifest);
        Assert.AreEqual(" [Python, Wisp]", tag);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubSkillStore : ISkillStore
    {
        private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.OrdinalIgnoreCase);

        public void Add(Skill skill) => _skills[skill.Name] = skill;
        public void AddResource(string skillName, string filename, string content)
        {
            if (!_resources.TryGetValue(skillName, out var files))
                _resources[skillName] = files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            files[filename] = content;
        }

        public Task SaveAsync(Skill skill) { _skills[skill.Name] = skill; return Task.CompletedTask; }
        public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources)
        {
            if (resources is null || resources.Count == 0)
            {
                // Preserve the existing manifest when no resources are provided,
                // mirroring FileSkillStore's behaviour.
                var existing = _skills.GetValueOrDefault(skill.Name);
                var skillToSave = skill.Manifest is null && existing?.Manifest is not null
                    ? skill with { Manifest = existing.Manifest }
                    : skill;
                _skills[skill.Name] = skillToSave;
                return Task.CompletedTask;
            }
            var manifest = resources.Select(r => new SkillResource(r.Filename, r.Type, r.Description)).ToList();
            _skills[skill.Name] = skill with { Manifest = manifest };
            return Task.CompletedTask;
        }
        public Task<Skill?> GetAsync(string name) => Task.FromResult(_skills.GetValueOrDefault(name));

        /// <summary>When set, <see cref="EditContentAsync"/> returns this instead of editing.</summary>
        public ContentEditResult? EditResult { get; set; }

        public Task<ContentEditResult> EditContentAsync(string name, string oldText, string newText, bool replaceAll = false)
        {
            if (EditResult is { } canned)
                return Task.FromResult(canned);

            if (!_skills.TryGetValue(name, out var existing))
                return Task.FromResult(ContentEditResult.Failed($"Skill '{name}' not found."));

            var edit = TextEdit.Apply(existing.Content, oldText, newText, replaceAll);
            if (!edit.IsSuccess)
                return Task.FromResult(ContentEditResult.Failed(edit.Error!));

            _skills[name] = existing with { Content = edit.Content!, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(ContentEditResult.Applied(
                edit.ReplacementCount, existing.Content.Length, edit.Content!.Length));
        }
        public Task<IReadOnlyList<Skill>> ListAsync() =>
            Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());
        public Task DeleteAsync(string name) { _skills.Remove(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task<string?> GetResourceAsync(string skillName, string filename)
        {
            if (_resources.TryGetValue(skillName, out var files) && files.TryGetValue(filename, out var content))
                return Task.FromResult<string?>(content);
            return Task.FromResult<string?>(null);
        }

        public Task<bool> AttachResourceAsync(string skillName, SkillResourceInput resource, SkillResource? manifestEntry = null)
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

            var oldManifest = existing.Manifest;
            var newManifest = oldManifest
                .Where(r => !string.Equals(r.Filename, filename, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (newManifest.Count == oldManifest.Count)
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

            var oldManifest = existing.Manifest;
            var newManifest = new List<SkillResource>(oldManifest.Count);
            var matched = false;
            foreach (var old in oldManifest)
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

    private sealed class StubChatClient : ILlmClient
    {
        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "stub summary")]));

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ModelTier tier, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }
}
