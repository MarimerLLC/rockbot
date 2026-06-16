using RockBot.Tools;

namespace RockBot.Tools.Tests;

[TestClass]
public class ToolProfileTests
{
    private static ToolRegistration Reg(string name, string source) =>
        new() { Name = name, Description = name, Source = source };

    [TestMethod]
    public void All_MatchesEverything()
    {
        Assert.IsTrue(ToolProfile.All.Matches(Reg("anything", "any-source")));
        Assert.IsTrue(ToolProfile.All.Matches(Reg("spawn_subagent", "subagent")));
    }

    [TestMethod]
    public void DeniedToolName_OverridesWildcardAllow()
    {
        var profile = ToolProfile.All.DenyingToolNames("blocked_tool");

        Assert.IsFalse(profile.Matches(Reg("blocked_tool", "web")));
        Assert.IsTrue(profile.Matches(Reg("other_tool", "web")));
    }

    [TestMethod]
    public void DeniedSource_OverridesWildcardAllow()
    {
        var profile = ToolProfile.All.DenyingSources("a2a");

        Assert.IsFalse(profile.Matches(Reg("invoke_agent", "a2a")));
        Assert.IsTrue(profile.Matches(Reg("web_search", "web")));
    }

    [TestMethod]
    public void DeniedToolName_BeatsExplicitAllowName()
    {
        // Deny takes precedence over an explicit allow of the same name.
        var profile = ToolProfile.All
            .AllowingOnlySources("web")
            .AllowingToolNames("special")
            .DenyingToolNames("special");

        Assert.IsFalse(profile.Matches(Reg("special", "a2a")));
    }

    [TestMethod]
    public void FailsClosed_WhenNoAllowMatches()
    {
        // Restricted to source "web" only — a tool from another source with no
        // explicit name allow is excluded even though nothing denies it.
        var profile = ToolProfile.All.AllowingOnlySources("web");

        Assert.IsTrue(profile.Matches(Reg("web_search", "web")));
        Assert.IsFalse(profile.Matches(Reg("read_file", "filesystem")));
    }

    [TestMethod]
    public void AllowingToolNames_AdmitsToolFromOtherwiseDeniedSource()
    {
        var profile = ToolProfile.All
            .AllowingOnlySources("web")
            .AllowingToolNames("read_file");

        Assert.IsTrue(profile.Matches(Reg("read_file", "filesystem")));
        Assert.IsFalse(profile.Matches(Reg("delete_file", "filesystem")));
    }

    [TestMethod]
    public void CompositionHelpers_DoNotMutateSource()
    {
        var derived = ToolProfile.All.DenyingSources("a2a").DenyingToolNames("x");

        // ToolProfile.All is the shared basis — composition must return new records.
        Assert.AreEqual(0, ToolProfile.All.DeniedSources.Count);
        Assert.AreEqual(0, ToolProfile.All.DeniedToolNames.Count);
        Assert.AreEqual(1, derived.DeniedSources.Count);
        Assert.AreEqual(1, derived.DeniedToolNames.Count);
    }

    [TestMethod]
    public void Named_SetsDisplayNameWithoutChangingRules()
    {
        var profile = ToolProfile.All.Named("Custom");

        Assert.AreEqual("Custom", profile.Name);
        Assert.IsTrue(profile.Matches(Reg("anything", "any")));
    }
}
