using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileSkillStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-skill-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Save / Get round-trip ─────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_And_GetAsync_RoundTrips()
    {
        var store = CreateStore();
        var skill = MakeSkill("plan-meeting", "Schedule meetings", "# Plan Meeting\n\nStep 1...");

        await store.SaveAsync(skill);
        var result = await store.GetAsync("plan-meeting");

        Assert.IsNotNull(result);
        Assert.AreEqual("plan-meeting", result.Name);
        Assert.AreEqual("Schedule meetings", result.Summary);
        Assert.AreEqual("# Plan Meeting\n\nStep 1...", result.Content);
    }

    [TestMethod]
    public async Task GetAsync_UnknownName_ReturnsNull()
    {
        var result = await CreateStore().GetAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SaveAsync_OverwritesExistingSkill()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("s1", "Old summary", "Old content"));
        await store.SaveAsync(MakeSkill("s1", "New summary", "New content"));

        var result = await store.GetAsync("s1");
        Assert.AreEqual("New summary", result!.Summary);
        Assert.AreEqual("New content", result.Content);
    }

    // ── File layout ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_CreatesJsonFile()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("plan-meeting", "summary", "content"));

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "plan-meeting.json")));
    }

    [TestMethod]
    public async Task SaveAsync_SubcategoryName_CreatesNestedFile()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research/summarize", "summary", "content"));

        Assert.IsTrue(File.Exists(
            Path.Combine(_tempDir, "research", "summarize.json")));
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListAsync_ReturnsAllSkillsAlphabetically()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("zebra", "z", "z"));
        await store.SaveAsync(MakeSkill("alpha", "a", "a"));
        await store.SaveAsync(MakeSkill("middle", "m", "m"));

        var list = await store.ListAsync();

        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("alpha", list[0].Name);
        Assert.AreEqual("middle", list[1].Name);
        Assert.AreEqual("zebra", list[2].Name);
    }

    [TestMethod]
    public async Task ListAsync_EmptyStore_ReturnsEmpty()
    {
        var list = await CreateStore().ListAsync();
        Assert.AreEqual(0, list.Count);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteAsync_RemovesSkill()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("s1", "summary", "content"));
        await store.DeleteAsync("s1");

        Assert.IsNull(await store.GetAsync("s1"));
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "s1.json")));
    }

    [TestMethod]
    public async Task DeleteAsync_NonexistentName_NoOp()
    {
        // Should not throw
        await CreateStore().DeleteAsync("ghost");
    }

    [TestMethod]
    public async Task DeleteAsync_DoesNotAffectOtherSkills()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("keep", "k", "k"));
        await store.SaveAsync(MakeSkill("remove", "r", "r"));
        await store.DeleteAsync("remove");

        Assert.IsNotNull(await store.GetAsync("keep"));
        Assert.IsNull(await store.GetAsync("remove"));
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Index_LoadedFromDisk_OnNewInstance()
    {
        var store1 = CreateStore();
        await store1.SaveAsync(MakeSkill("persisted", "summary", "I survive restarts"));

        var store2 = CreateStore();
        var result = await store2.GetAsync("persisted");

        Assert.IsNotNull(result);
        Assert.AreEqual("I survive restarts", result.Content);
    }

    [TestMethod]
    public async Task MalformedJsonFile_Skipped()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "not json {{{");

        var store = CreateStore();
        await store.SaveAsync(MakeSkill("good", "summary", "content"));

        var list = await store.ListAsync();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("good", list[0].Name);
    }

    // ── Name validation ───────────────────────────────────────────────────────

    [TestMethod]
    public void ValidateName_RejectsTraversal()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateName("../../etc/passwd"));
    }

    [TestMethod]
    public void ValidateName_RejectsAbsolutePath()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateName("/absolute/path"));
    }

    [TestMethod]
    public void ValidateName_RejectsEmptyString()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateName(""));
    }

    [TestMethod]
    public void ValidateName_RejectsInvalidCharacters()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateName("plan meeting!"));
    }

    [TestMethod]
    public void ValidateName_AcceptsValidNames()
    {
        // Should not throw
        FileSkillStore.ValidateName("plan-meeting");
        FileSkillStore.ValidateName("research/summarize-paper");
        FileSkillStore.ValidateName("A_B/c-d/E123");
    }

    // ── SearchAsync (BM25) ────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_ReturnsRelevantSkills()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("plan-meeting", "Schedule meetings and invite attendees", "content"));
        await store.SaveAsync(MakeSkill("send-email", "Send an email to a recipient", "content"));
        await store.SaveAsync(MakeSkill("summarize-paper", "Summarize a research paper", "content"));

        var results = await store.SearchAsync("meeting schedule", maxResults: 5);

        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("plan-meeting", results[0].Name);
    }

    [TestMethod]
    public async Task SearchAsync_EmptyStore_ReturnsEmpty()
    {
        var results = await CreateStore().SearchAsync("anything", maxResults: 5);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_NoMatchingSkills_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("plan-meeting", "Schedule meetings", "content"));

        var results = await store.SearchAsync("xyzzy", maxResults: 5);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_RespectsMaxResults()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("plan-meeting", "Schedule meetings efficiently", "content"));
        await store.SaveAsync(MakeSkill("book-meeting", "Book a meeting room for meetings", "content"));
        await store.SaveAsync(MakeSkill("cancel-meeting", "Cancel a scheduled meeting", "content"));

        var results = await store.SearchAsync("meeting", maxResults: 2);

        Assert.IsTrue(results.Count <= 2);
    }

    [TestMethod]
    public void GetDocumentText_CombinesNameAndSummary()
    {
        var skill = MakeSkill("plan-meeting", "Schedule meetings", "content");
        var text = FileSkillStore.GetDocumentText(skill);

        Assert.IsTrue(text.Contains("plan"));
        Assert.IsTrue(text.Contains("meeting"));
        Assert.IsTrue(text.Contains("Schedule"));
    }

    [TestMethod]
    public void GetDocumentText_EmptySummary_ReturnsNameOnly()
    {
        var skill = MakeSkill("plan-meeting", "", "content");
        var text = FileSkillStore.GetDocumentText(skill);

        Assert.IsTrue(text.Contains("plan"));
        Assert.IsTrue(text.Contains("meeting"));
    }

    [TestMethod]
    public void WithSkills_RegistersISkillStore()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithSkills();
        });

        var provider = services.BuildServiceProvider();
        Assert.IsNotNull(provider.GetService<ISkillStore>());
    }

    [TestMethod]
    public void WithSkills_CustomOptions_Configures()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithSkills(o => o.BasePath = "/custom/skills");
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SkillOptions>>();
        Assert.AreEqual("/custom/skills", options.Value.BasePath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileSkillStore CreateStore() =>
        new(Options.Create(new SkillOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<FileSkillStore>.Instance);

    private static Skill MakeSkill(string name, string summary, string content) =>
        new(name, summary, content, DateTimeOffset.UtcNow);
}
