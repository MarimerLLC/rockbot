namespace RockBot.Host.Tests;

[TestClass]
public class DefaultSystemPromptBuilderTests
{
    [TestMethod]
    public void Build_PrependsAgentName()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul content.");
        var directives = new AgentProfileDocument("directives", null, [], "Directive content.");
        var profile = new AgentProfile(soul, directives);
        var identity = new AgentIdentity("echo-agent");
        var builder = new DefaultSystemPromptBuilder();

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.StartsWith("You are echo-agent."));
    }

    [TestMethod]
    public void Build_IncludesAllDocuments()
    {
        var soul = new AgentProfileDocument("soul", null, [], "I am a helpful agent.");
        var directives = new AgentProfileDocument("directives", null, [], "Follow these rules.");
        var profile = new AgentProfile(soul, directives);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder();

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.Contains("I am a helpful agent."));
        Assert.IsTrue(prompt.Contains("Follow these rules."));
    }

    [TestMethod]
    public void Build_IncludesStyleWhenPresent()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul.");
        var directives = new AgentProfileDocument("directives", null, [], "Directives.");
        var style = new AgentProfileDocument("style", null, [], "Be witty.");
        var profile = new AgentProfile(soul, directives, style);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder();

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.Contains("Soul."));
        Assert.IsTrue(prompt.Contains("Directives."));
        Assert.IsTrue(prompt.Contains("Be witty."));
    }

    [TestMethod]
    public void Build_DocumentsAppearInOrder()
    {
        var soul = new AgentProfileDocument("soul", null, [], "AAA-SOUL");
        var directives = new AgentProfileDocument("directives", null, [], "BBB-DIRECTIVES");
        var style = new AgentProfileDocument("style", null, [], "CCC-STYLE");
        var profile = new AgentProfile(soul, directives, style);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder();

        var prompt = builder.Build(profile, identity);

        var soulIdx = prompt.IndexOf("AAA-SOUL");
        var directivesIdx = prompt.IndexOf("BBB-DIRECTIVES");
        var styleIdx = prompt.IndexOf("CCC-STYLE");

        Assert.IsTrue(soulIdx < directivesIdx, "Soul should appear before directives");
        Assert.IsTrue(directivesIdx < styleIdx, "Directives should appear before style");
    }
}
