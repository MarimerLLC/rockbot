namespace RockBot.A2A.Tests;

[TestClass]
public class AgentDirectoryTests
{
    private readonly AgentDirectory _directory = new();

    private static AgentCard CreateCard(string name, params AgentSkill[] skills) =>
        new()
        {
            AgentName = name,
            Description = $"{name} agent",
            Skills = skills.Length > 0 ? skills : null
        };

    private static AgentSkill CreateSkill(string id) =>
        new()
        {
            Id = id,
            Name = id,
            Description = $"Skill {id}"
        };

    [TestMethod]
    public void StoresAndRetrievesAgentCards()
    {
        var card = CreateCard("agent-a");
        _directory.AddOrUpdate(card);

        var retrieved = _directory.GetAgent("agent-a");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("agent-a", retrieved.AgentName);
    }

    [TestMethod]
    public void GetAgent_ReturnsCaseInsensitive()
    {
        _directory.AddOrUpdate(CreateCard("Agent-B"));

        Assert.IsNotNull(_directory.GetAgent("agent-b"));
        Assert.IsNotNull(_directory.GetAgent("AGENT-B"));
    }

    [TestMethod]
    public void OverwritesCard_OnReAnnounce()
    {
        _directory.AddOrUpdate(CreateCard("agent-c") with { Version = "1.0" });
        _directory.AddOrUpdate(CreateCard("agent-c") with { Version = "2.0" });

        var card = _directory.GetAgent("agent-c");
        Assert.IsNotNull(card);
        Assert.AreEqual("2.0", card.Version);
    }

    [TestMethod]
    public void GetAllAgents_ReturnsAllCards()
    {
        _directory.AddOrUpdate(CreateCard("agent-1"));
        _directory.AddOrUpdate(CreateCard("agent-2"));
        _directory.AddOrUpdate(CreateCard("agent-3"));

        var all = _directory.GetAllAgents();
        Assert.AreEqual(3, all.Count);
    }

    [TestMethod]
    public void FindBySkill_ReturnsMatchingAgents()
    {
        _directory.AddOrUpdate(CreateCard("agent-x", CreateSkill("summarize"), CreateSkill("translate")));
        _directory.AddOrUpdate(CreateCard("agent-y", CreateSkill("summarize")));
        _directory.AddOrUpdate(CreateCard("agent-z", CreateSkill("code-review")));

        var matches = _directory.FindBySkill("summarize");
        Assert.AreEqual(2, matches.Count);
        Assert.IsTrue(matches.Any(c => c.AgentName == "agent-x"));
        Assert.IsTrue(matches.Any(c => c.AgentName == "agent-y"));
    }

    [TestMethod]
    public void FindBySkill_IsCaseInsensitive()
    {
        _directory.AddOrUpdate(CreateCard("agent-1", CreateSkill("Summarize")));

        var matches = _directory.FindBySkill("summarize");
        Assert.AreEqual(1, matches.Count);
    }

    [TestMethod]
    public void ReturnsNull_ForUnknownAgent()
    {
        Assert.IsNull(_directory.GetAgent("nonexistent"));
    }

    [TestMethod]
    public void FindBySkill_ReturnsEmpty_ForUnknownSkill()
    {
        _directory.AddOrUpdate(CreateCard("agent-1", CreateSkill("summarize")));

        var matches = _directory.FindBySkill("nonexistent-skill");
        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindBySkill_ReturnsEmpty_WhenAgentHasNoSkills()
    {
        _directory.AddOrUpdate(CreateCard("agent-no-skills"));

        var matches = _directory.FindBySkill("anything");
        Assert.AreEqual(0, matches.Count);
    }
}
