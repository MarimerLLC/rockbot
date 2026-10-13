namespace RockBot.Host.Tests;

/// <summary>
/// The lessons block a tool guide carries wherever it's delivered (#699): the companion's text
/// without its stub preamble, or a pointer when the text is too long.
/// </summary>
[TestClass]
public class CodeOwnedSkillsTests
{
    private static Skill CompanionWith(string lessons)
    {
        var stub = CodeOwnedSkills.Companion(null, "mcp", DateTimeOffset.UtcNow);
        return stub with { Content = stub.Content + "\n\n" + lessons };
    }

    [TestMethod]
    public void LessonsBlock_InlinesTheLessons_WithoutTheStubPreamble()
    {
        var block = CodeOwnedSkills.LessonsBlock(CompanionWith("## Wisp Failure Pattern\n\nPass the server."), "mcp");

        Assert.AreEqual("---\n## Lessons learned (skill: mcp-learned)\n\n## Wisp Failure Pattern\n\nPass the server.", block);
    }

    [TestMethod]
    public void LessonsBlock_HandWrittenCompanion_IsInlinedWhole()
    {
        var companion = new Skill("mcp-learned", "s", "# My notes\n\nUse typed tools.", DateTimeOffset.UtcNow);

        StringAssert.EndsWith(CodeOwnedSkills.LessonsBlock(companion, "mcp"), "# My notes\n\nUse typed tools.");
    }

    [TestMethod]
    public void LessonsBlock_TooLong_GivesTheSummaryAndAPointer()
    {
        var companion = CompanionWith(new string('x', CodeOwnedSkills.LessonsInlineLimit + 1)) with
        {
            Summary = "Typed-tool pitfalls"
        };

        var block = CodeOwnedSkills.LessonsBlock(companion, "mcp");

        StringAssert.Contains(block, "Typed-tool pitfalls");
        StringAssert.Contains(block, "get_skill(\"mcp-learned\")");
        Assert.IsFalse(block.Contains("xxxx"));
    }

    [TestMethod]
    public async Task LessonsBlockAsync_NoCompanionOrOnlyTheStub_IsNull()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        Assert.IsNull(await CodeOwnedSkills.LessonsBlockAsync(store, "mcp", markUsed: true));

        await store.SaveAsync(CodeOwnedSkills.Companion(null, "mcp", DateTimeOffset.UtcNow));
        Assert.IsNull(await CodeOwnedSkills.LessonsBlockAsync(store, "mcp", markUsed: true));
        Assert.IsNull((await store.GetAsync("mcp-learned"))!.LastUsedAt);
    }

    [TestMethod]
    public async Task LessonsBlockAsync_MarkUsed_RecordsUseOfTheCompanion()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(CompanionWith("A lesson."));

        Assert.IsNotNull(await CodeOwnedSkills.LessonsBlockAsync(store, "mcp", markUsed: false));
        Assert.IsNull((await store.GetAsync("mcp-learned"))!.LastUsedAt);

        Assert.IsNotNull(await CodeOwnedSkills.LessonsBlockAsync(store, "mcp", markUsed: true));
        Assert.IsNotNull((await store.GetAsync("mcp-learned"))!.LastUsedAt);
    }
}
