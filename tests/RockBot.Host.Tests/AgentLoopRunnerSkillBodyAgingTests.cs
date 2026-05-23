using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for skill-body aging — BM25-recalled skill bodies (system messages formatted
/// "Skill: {name}\n{content}") get unloaded after N inner-loop iterations of non-use.
/// Affects both the primary agent and subagents (same code path).
/// </summary>
[TestClass]
public class AgentLoopRunnerSkillBodyAgingTests
{
    [TestMethod]
    public void Age_BelowThreshold_SkillBodyRetained()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.System, "Skill: mcp/calendar-mcp\n# Calendar MCP body content"),
            new(ChatRole.User, "do the thing"),
        };

        // Threshold of 5. After 3 unrelated tool calls, age should be 2 (< threshold).
        for (var i = 0; i < 3; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 5, logger: NullLogger.Instance);
        }

        Assert.IsTrue(messages.Any(m => m.Text?.StartsWith("Skill: mcp/calendar-mcp") == true),
            "Skill body should remain in context when within the unload threshold.");
        Assert.AreEqual(3, state.CurrentIteration);
    }

    [TestMethod]
    public void Age_PastThreshold_SkillBodyUnloaded()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.System, "Skill: mcp/calendar-mcp\n# Calendar MCP body content"),
            new(ChatRole.User, "do the thing"),
        };
        var initialCount = messages.Count;

        // Threshold of 3. Iteration 1: register skill at iter=1. Iter 2,3,4: age 1,2,3 (≤ 3,
        // not yet unloaded). Iter 5: age 4 (> 3, UNLOADED on this call).
        var removed = 0;
        for (var i = 0; i < 5; i++)
        {
            removed += AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 3, logger: NullLogger.Instance);
        }

        Assert.AreEqual(1, removed, "Exactly one skill body should have been unloaded.");
        Assert.AreEqual(initialCount - 1, messages.Count);
        Assert.IsFalse(messages.Any(m => m.Text?.StartsWith("Skill: mcp/calendar-mcp") == true),
            "Skill body should be removed after exceeding the unload threshold.");
        Assert.IsFalse(state.LastUseIteration.ContainsKey("mcp/calendar-mcp"),
            "Tracker entry should be cleared after unload so the next BM25 push starts fresh.");
        // Other messages must be untouched.
        Assert.IsTrue(messages.Any(m => m.Text == "preamble"));
        Assert.IsTrue(messages.Any(m => m.Role == ChatRole.User && m.Text == "do the thing"));
    }

    [TestMethod]
    public void Age_GetSkillCallRefreshesUse_BodyRetainedPastThreshold()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Skill: mcp/calendar-mcp\n# Calendar MCP body content"),
            new(ChatRole.User, "do the thing"),
        };

        // Iter 1-3: unrelated calls (age accumulates).
        for (var i = 0; i < 3; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 3, logger: NullLogger.Instance);
        }
        // Iter 4: model calls get_skill(name=mcp/calendar-mcp) — refreshes the clock.
        var refreshArgs = new Dictionary<string, object?> { ["name"] = "mcp/calendar-mcp" };
        var removedAtRefresh = AgentLoopRunner.RegisterAndAgeSkillBodies(
            messages, state, toolName: "get_skill", toolArgs: refreshArgs,
            unloadAfter: 3, logger: NullLogger.Instance);
        Assert.AreEqual(0, removedAtRefresh, "Refresh call must not unload the body it refreshes.");

        // Iter 5-7: three more unrelated calls (age from refresh = 3, still ≤ threshold).
        for (var i = 0; i < 3; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 3, logger: NullLogger.Instance);
        }

        Assert.IsTrue(messages.Any(m => m.Text?.StartsWith("Skill: mcp/calendar-mcp") == true),
            "get_skill refresh should reset the clock — body must still be present.");
    }

    [TestMethod]
    public void Age_MultipleSkills_TrackedIndependently()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Skill: skill-a\n# Body A"),
            new(ChatRole.System, "Skill: skill-b\n# Body B"),
            new(ChatRole.User, "do the thing"),
        };

        // Iter 1: discover both at iter 1.
        AgentLoopRunner.RegisterAndAgeSkillBodies(
            messages, state, toolName: "web_search", toolArgs: null,
            unloadAfter: 3, logger: NullLogger.Instance);

        // Iter 2,3: model refreshes skill-a only.
        for (var i = 0; i < 2; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "get_skill",
                toolArgs: new Dictionary<string, object?> { ["name"] = "skill-a" },
                unloadAfter: 3, logger: NullLogger.Instance);
        }

        // Iter 4-6: only skill-b ages out (skill-a was refreshed at iter 3).
        for (var i = 0; i < 3; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 3, logger: NullLogger.Instance);
        }

        Assert.IsTrue(messages.Any(m => m.Text?.StartsWith("Skill: skill-a") == true),
            "skill-a was refreshed at iter 3 — should still be present at iter 6.");
        Assert.IsFalse(messages.Any(m => m.Text?.StartsWith("Skill: skill-b") == true),
            "skill-b was last referenced at iter 1 — should be unloaded.");
    }

    [TestMethod]
    public void Age_UnloadAfterZero_NothingUnloaded()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Skill: skill-a\n# Body A"),
            new(ChatRole.User, "do the thing"),
        };

        // 100 iterations with threshold 0 — nothing should ever unload.
        for (var i = 0; i < 100; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 0, logger: NullLogger.Instance);
        }

        Assert.IsTrue(messages.Any(m => m.Text?.StartsWith("Skill: skill-a") == true),
            "unloadAfter=0 disables aging — body must persist regardless of iteration count.");
    }

    [TestMethod]
    public void Age_NonSkillSystemMessages_Untouched()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Skill: skill-a\n# Body A"),
            new(ChatRole.System, "Some other system message about skills in general"),
            new(ChatRole.System, "Skills are useful tools"), // text contains "Skill" but no "Skill: name\n" prefix
            new(ChatRole.User, "do the thing"),
        };

        for (var i = 0; i < 10; i++)
        {
            AgentLoopRunner.RegisterAndAgeSkillBodies(
                messages, state, toolName: "web_search", toolArgs: null,
                unloadAfter: 3, logger: NullLogger.Instance);
        }

        Assert.IsFalse(messages.Any(m => m.Text?.StartsWith("Skill: skill-a") == true),
            "skill-a body should age out.");
        Assert.IsTrue(messages.Any(m => m.Text == "Some other system message about skills in general"),
            "Unrelated system messages must not be removed.");
        Assert.IsTrue(messages.Any(m => m.Text == "Skills are useful tools"),
            "System messages without the exact 'Skill: name\\n' prefix must not be removed.");
    }

    [TestMethod]
    public void Age_GetSkillWithUnknownArgName_DoesNotCrash()
    {
        var state = new LoadedSkillsContext.State();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Skill: skill-a\n# Body A"),
        };

        // get_skill called with no recognised "name" argument — must not crash, must not
        // refresh any skill.
        var oddArgs = new Dictionary<string, object?> { ["wrong_key"] = "skill-a" };
        AgentLoopRunner.RegisterAndAgeSkillBodies(
            messages, state, toolName: "get_skill", toolArgs: oddArgs,
            unloadAfter: 5, logger: NullLogger.Instance);

        // skill-a was still discovered (it's in the messages), but the refresh didn't fire.
        Assert.AreEqual(1, state.LastUseIteration["skill-a"]);
    }

    [TestMethod]
    public void TryExtractLoadedSkillName_VariousFormats()
    {
        // Happy path.
        Assert.IsTrue(AgentLoopRunner.TryExtractLoadedSkillName(
            new ChatMessage(ChatRole.System, "Skill: mcp/calendar-mcp\nBody"), out var name1));
        Assert.AreEqual("mcp/calendar-mcp", name1);

        // No newline = not a body message (could be inline mention).
        Assert.IsFalse(AgentLoopRunner.TryExtractLoadedSkillName(
            new ChatMessage(ChatRole.System, "Skill: alone-on-one-line"), out _));

        // Wrong role.
        Assert.IsFalse(AgentLoopRunner.TryExtractLoadedSkillName(
            new ChatMessage(ChatRole.User, "Skill: foo\nbar"), out _));

        // Different prefix.
        Assert.IsFalse(AgentLoopRunner.TryExtractLoadedSkillName(
            new ChatMessage(ChatRole.System, "Skills:\nfoo"), out _));

        // Empty name between prefix and newline.
        Assert.IsFalse(AgentLoopRunner.TryExtractLoadedSkillName(
            new ChatMessage(ChatRole.System, "Skill: \nbody"), out _));
    }

    [TestMethod]
    public void TryGetSkillNameArgument_CaseInsensitive()
    {
        Assert.AreEqual("foo", AgentLoopRunner.TryGetSkillNameArgument(
            new Dictionary<string, object?> { ["name"] = "foo" }));
        Assert.AreEqual("bar", AgentLoopRunner.TryGetSkillNameArgument(
            new Dictionary<string, object?> { ["Name"] = "bar" }));
        Assert.AreEqual("baz", AgentLoopRunner.TryGetSkillNameArgument(
            new Dictionary<string, object?> { ["NAME"] = "baz" }));
        Assert.IsNull(AgentLoopRunner.TryGetSkillNameArgument(
            new Dictionary<string, object?> { ["other"] = "qux" }));
        Assert.IsNull(AgentLoopRunner.TryGetSkillNameArgument(null));
    }
}
