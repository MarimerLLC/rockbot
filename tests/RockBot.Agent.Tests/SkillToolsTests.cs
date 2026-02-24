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
    public async Task GetSkill_UnknownSkill_ReturnsNotFound()
    {
        var tools = new SkillTools(new StubSkillStore(), new StubChatClient(), NullLogger<SkillTools>.Instance);
        var result = await tools.GetSkill("nonexistent");

        Assert.IsTrue(result.Contains("not found"));
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

        public void Add(Skill skill) => _skills[skill.Name] = skill;

        public Task SaveAsync(Skill skill) { _skills[skill.Name] = skill; return Task.CompletedTask; }
        public Task<Skill?> GetAsync(string name) => Task.FromResult(_skills.GetValueOrDefault(name));
        public Task<IReadOnlyList<Skill>> ListAsync() =>
            Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());
        public Task DeleteAsync(string name) { _skills.Remove(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
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
