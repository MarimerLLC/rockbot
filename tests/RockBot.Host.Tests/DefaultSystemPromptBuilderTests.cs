namespace RockBot.Host.Tests;

[TestClass]
public class DefaultSystemPromptBuilderTests
{
    private static ProfileHolder CreateHolder(AgentProfile profile)
    {
        var holder = new ProfileHolder();
        holder.Update(profile);
        return holder;
    }

    [TestMethod]
    public void Build_PrependsAgentName()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul content.");
        var directives = new AgentProfileDocument("directives", null, [], "Directive content.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("echo-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.StartsWith("You are echo-agent."));
    }

    [TestMethod]
    public void Build_IncludesAllDocuments()
    {
        var soul = new AgentProfileDocument("soul", null, [], "I am a helpful agent.");
        var directives = new AgentProfileDocument("directives", null, [], "Follow these rules.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

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
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

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
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

        var prompt = builder.Build(profile, identity);

        var soulIdx = prompt.IndexOf("AAA-SOUL");
        var directivesIdx = prompt.IndexOf("BBB-DIRECTIVES");
        var styleIdx = prompt.IndexOf("CCC-STYLE");

        Assert.IsTrue(soulIdx < directivesIdx, "Soul should appear before directives");
        Assert.IsTrue(directivesIdx < styleIdx, "Directives should appear before style");
    }

    [TestMethod]
    public void Build_InvalidatesCacheWhenProfileVersionChanges()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Original soul.");
        var directives = new AgentProfileDocument("directives", null, [], "Original directives.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

        var prompt1 = builder.Build(profile, identity);
        Assert.IsTrue(prompt1.Contains("Original soul."));

        // Update profile in holder — simulates hot-reload
        var newSoul = new AgentProfileDocument("soul", null, [], "Updated soul.");
        var newDirectives = new AgentProfileDocument("directives", null, [], "Updated directives.");
        var newProfile = new AgentProfile(newSoul, newDirectives);
        holder.Update(newProfile);

        var prompt2 = builder.Build(newProfile, identity);

        Assert.IsTrue(prompt2.Contains("Updated soul."), "Should contain updated soul content");
        Assert.IsTrue(prompt2.Contains("Updated directives."), "Should contain updated directives content");
        Assert.IsFalse(prompt2.Contains("Original soul."), "Should not contain original soul content");
    }

    [TestMethod]
    public void Build_ReturnsCachedWhenVersionUnchanged()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul.");
        var directives = new AgentProfileDocument("directives", null, [], "Directives.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder);

        var prompt1 = builder.Build(profile, identity);
        var prompt2 = builder.Build(profile, identity);

        Assert.IsTrue(ReferenceEquals(prompt1, prompt2), "Should return same cached string instance");
    }
}
