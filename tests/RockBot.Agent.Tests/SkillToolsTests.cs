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
