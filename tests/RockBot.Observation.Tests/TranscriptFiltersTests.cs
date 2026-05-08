namespace RockBot.Observation.Tests;

[TestClass]
public class TranscriptFiltersTests
{
    private static TranscriptTurn Turn(string source, string role, string content = "x", string id = "t1") =>
        new("conv1", id, source, role, content, DateTimeOffset.UtcNow);

    [TestMethod]
    public void Everything_PassesAllTurnsThrough()
    {
        var turns = new[]
        {
            Turn(TranscriptSources.User, "user", id: "t1"),
            Turn(TranscriptSources.Agent, "assistant", id: "t2"),
            Turn(TranscriptSources.Agent, "tool", id: "t3"),
            Turn(TranscriptSources.ScheduledTask, "assistant", id: "t4"),
        };

        var filtered = TranscriptFilters.Everything.Filter(turns).ToList();

        Assert.AreEqual(4, filtered.Count);
        CollectionAssert.AreEqual(
            turns.Select(t => t.TurnId).ToArray(),
            filtered.Select(t => t.TurnId).ToArray());
    }

    [TestMethod]
    public void UserAuthored_KeepsUserAndAgentAssistantTurns()
    {
        var turns = new[]
        {
            Turn(TranscriptSources.User, "user", id: "u1"),
            Turn(TranscriptSources.Agent, "assistant", id: "a1"),
            Turn(TranscriptSources.User, "user", id: "u2"),
            Turn(TranscriptSources.Agent, "assistant", id: "a2"),
        };

        var filtered = TranscriptFilters.UserAuthored.Filter(turns)
            .Select(t => t.TurnId)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "u1", "a1", "u2", "a2" }, filtered);
    }

    [TestMethod]
    public void UserAuthored_DropsToolAndScheduledTurns()
    {
        var turns = new[]
        {
            Turn(TranscriptSources.User, "user", id: "u1"),
            Turn(TranscriptSources.Agent, "tool", id: "tool1"),
            Turn(TranscriptSources.Agent, "assistant", id: "a1"),
            Turn(TranscriptSources.ScheduledTask, "assistant", id: "sched1"),
            Turn(TranscriptSources.Heartbeat, "assistant", id: "hb1"),
        };

        var filtered = TranscriptFilters.UserAuthored.Filter(turns)
            .Select(t => t.TurnId)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "u1", "a1" }, filtered,
            "Tool, scheduled-task, and heartbeat turns must be excluded");
    }

    [TestMethod]
    public void UserAuthored_AssistantRoleIsCaseInsensitive()
    {
        var turns = new[]
        {
            Turn(TranscriptSources.Agent, "Assistant", id: "a1"),
            Turn(TranscriptSources.Agent, "ASSISTANT", id: "a2"),
            Turn(TranscriptSources.Agent, "assistant", id: "a3"),
        };

        var filtered = TranscriptFilters.UserAuthored.Filter(turns)
            .Select(t => t.TurnId)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "a1", "a2", "a3" }, filtered);
    }

    [TestMethod]
    public void UserAuthored_UnknownSource_Excluded()
    {
        var turns = new[]
        {
            Turn(TranscriptSources.User, "user", id: "u1"),
            Turn("custom-source", "assistant", id: "x1"),
        };

        var filtered = TranscriptFilters.UserAuthored.Filter(turns)
            .Select(t => t.TurnId)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "u1" }, filtered,
            "Filter defaults to excluding unrecognised sources, not including them");
    }

    [TestMethod]
    public void UserAuthored_EmptyInput_EmptyOutput()
    {
        var filtered = TranscriptFilters.UserAuthored.Filter([]).ToList();
        Assert.AreEqual(0, filtered.Count);
    }
}
