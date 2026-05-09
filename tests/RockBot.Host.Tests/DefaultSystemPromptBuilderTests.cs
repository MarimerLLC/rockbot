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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("echo-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

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
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

        var prompt1 = builder.Build(profile, identity);
        var prompt2 = builder.Build(profile, identity);

        Assert.IsTrue(ReferenceEquals(prompt1, prompt2), "Should return same cached string instance");
    }

    [TestMethod]
    public void Build_UsesDisplayNameWhenSet()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul content.");
        var directives = new AgentProfileDocument("directives", null, [], "Directive content.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var nameHolder = new AgentNameHolder();
        nameHolder.Update("CustomBot");
        var identity = new AgentIdentity("default-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.StartsWith("You are CustomBot."));
        Assert.IsFalse(prompt.Contains("default-agent"));
    }

    [TestMethod]
    public void Build_FallsBackToIdentityNameWhenNoDisplayName()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul content.");
        var directives = new AgentProfileDocument("directives", null, [], "Directive content.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("fallback-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

        var prompt = builder.Build(profile, identity);

        Assert.IsTrue(prompt.StartsWith("You are fallback-agent."));
    }

    [TestMethod]
    public void Build_InvalidatesCacheWhenNameVersionChanges()
    {
        var soul = new AgentProfileDocument("soul", null, [], "Soul.");
        var directives = new AgentProfileDocument("directives", null, [], "Directives.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(holder, nameHolder, Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()));

        var prompt1 = builder.Build(profile, identity);
        Assert.IsTrue(prompt1.StartsWith("You are test-agent."));

        nameHolder.Update("NewName");

        var prompt2 = builder.Build(profile, identity);
        Assert.IsTrue(prompt2.StartsWith("You are NewName."));
        Assert.IsFalse(ReferenceEquals(prompt1, prompt2));
    }

    // ── Phase 4 PromptBuilderHint integration ─────────────────────────────────

    [TestMethod]
    public void Build_NullCategory_BehavesLikeOriginalOverload()
    {
        var (builder, profile, identity, _) = NewBuilderWithTempBase();
        var withoutCategory = builder.Build(profile, identity);
        var withNullCategory = builder.Build(profile, identity, category: null);

        Assert.AreEqual(withoutCategory, withNullCategory);
    }

    [TestMethod]
    public async Task Build_WithCategory_AppendsHintFileContent()
    {
        var (builder, profile, identity, tempDir) = NewBuilderWithTempBase();
        try
        {
            await WriteHintAsync(tempDir, "patrol", "HINT_BODY_FOR_PATROL");
            var prompt = builder.Build(profile, identity, category: "patrol");

            StringAssert.Contains(prompt, "HINT_BODY_FOR_PATROL");
            StringAssert.Contains(prompt, "---");
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [TestMethod]
    public async Task Build_DifferentCategories_PickDifferentHints()
    {
        var (builder, profile, identity, tempDir) = NewBuilderWithTempBase();
        try
        {
            await WriteHintAsync(tempDir, "patrol", "PATROL_HINT");
            await WriteHintAsync(tempDir, "session", "SESSION_HINT");

            var patrolPrompt = builder.Build(profile, identity, category: "patrol");
            var sessionPrompt = builder.Build(profile, identity, category: "session");

            StringAssert.Contains(patrolPrompt, "PATROL_HINT");
            Assert.IsFalse(patrolPrompt.Contains("SESSION_HINT"));
            StringAssert.Contains(sessionPrompt, "SESSION_HINT");
            Assert.IsFalse(sessionPrompt.Contains("PATROL_HINT"));
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [TestMethod]
    public async Task Build_HintFileChanged_RebuildsPrompt()
    {
        var (builder, profile, identity, tempDir) = NewBuilderWithTempBase();
        try
        {
            await WriteHintAsync(tempDir, "patrol", "v1");
            var first = builder.Build(profile, identity, category: "patrol");
            StringAssert.Contains(first, "v1");

            await Task.Delay(50);
            await WriteHintAsync(tempDir, "patrol", "v2");

            var second = builder.Build(profile, identity, category: "patrol");
            StringAssert.Contains(second, "v2");
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [TestMethod]
    public void Build_UnsafeCategory_DoesNotInjectHint()
    {
        var (builder, profile, identity, tempDir) = NewBuilderWithTempBase();
        try
        {
            // No file exists, but the category itself is unsafe — must not throw or escape.
            var prompt = builder.Build(profile, identity, category: "../escape");
            StringAssert.Contains(prompt, "Soul.");
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    private static async Task WriteHintAsync(string baseDir, string category, string body)
    {
        var dir = Path.Combine(baseDir, "prompt-hints");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, category + ".md"), body);
    }

    private static (DefaultSystemPromptBuilder builder, AgentProfile profile, AgentIdentity identity, string tempDir) NewBuilderWithTempBase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rockbot-promptbuilder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var soul = new AgentProfileDocument("soul", null, [], "Soul.");
        var directives = new AgentProfileDocument("directives", null, [], "Directives.");
        var profile = new AgentProfile(soul, directives);
        var holder = CreateHolder(profile);
        var nameHolder = new AgentNameHolder();
        var identity = new AgentIdentity("test-agent");
        var builder = new DefaultSystemPromptBuilder(
            holder,
            nameHolder,
            Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions { BasePath = tempDir }));
        return (builder, profile, identity, tempDir);
    }

    [TestMethod]
    public void DerivePromptCategory_TopLevelSegment_IsCategory()
    {
        Assert.AreEqual("session", AgentContextBuilder.DerivePromptCategory(null));
        Assert.AreEqual("session", AgentContextBuilder.DerivePromptCategory(""));
        Assert.AreEqual("session", AgentContextBuilder.DerivePromptCategory("session/abc"));
        Assert.AreEqual("patrol", AgentContextBuilder.DerivePromptCategory("patrol/heartbeat"));
        Assert.AreEqual("subagent", AgentContextBuilder.DerivePromptCategory("subagent/task1"));
    }
}
