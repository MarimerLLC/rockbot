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

    // ── SeeAlso round-trip ────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_And_GetAsync_PreservesSeeAlso()
    {
        var store = CreateStore();
        var skill = MakeSkillWithSeeAlso("mcp/email", "Send emails via MCP", "# Send Email\n\nStep 1...",
            "mcp/calendar", "mcp/guide");

        await store.SaveAsync(skill);
        var result = await store.GetAsync("mcp/email");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.SeeAlso);
        Assert.AreEqual(2, result.SeeAlso.Count);
        CollectionAssert.Contains(result.SeeAlso.ToList(), "mcp/calendar");
        CollectionAssert.Contains(result.SeeAlso.ToList(), "mcp/guide");
    }

    [TestMethod]
    public async Task SaveAsync_And_GetAsync_NullSeeAlso_RoundTrips()
    {
        var store = CreateStore();
        var skill = MakeSkill("plan-meeting", "Schedule meetings", "# Plan Meeting\n\nStep 1...");

        await store.SaveAsync(skill);
        var result = await store.GetAsync("plan-meeting");

        Assert.IsNotNull(result);
        // SeeAlso should be null (not set) on round-trip
        Assert.IsNull(result.SeeAlso);
    }

    [TestMethod]
    public async Task SeeAlso_PersistedAcrossInstances()
    {
        var store1 = CreateStore();
        await store1.SaveAsync(MakeSkillWithSeeAlso("mcp/email", "Send email", "content", "mcp/guide"));

        var store2 = CreateStore();
        var result = await store2.GetAsync("mcp/email");

        Assert.IsNotNull(result?.SeeAlso);
        CollectionAssert.Contains(result!.SeeAlso!.ToList(), "mcp/guide");
    }

    // ── Resources ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_WithResources_WritesResourceFiles()
    {
        var store = CreateStore();
        var skill = MakeSkill("my-skill", "summary", "content");
        var resources = new List<SkillResourceInput>
        {
            new("script.py", SkillResourceType.Python, "A helper script", "print('hello')")
        };

        await store.SaveAsync(skill, resources);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "my-skill.resources", "script.py")));
        Assert.AreEqual("print('hello')", await File.ReadAllTextAsync(Path.Combine(_tempDir, "my-skill.resources", "script.py")));
    }

    [TestMethod]
    public async Task SaveAsync_WithResources_StoresManifestInSkillJson()
    {
        var store = CreateStore();
        var skill = MakeSkill("my-skill", "summary", "content");
        var resources = new List<SkillResourceInput>
        {
            new("script.py", SkillResourceType.Python, "A helper script", "print('hello')"),
            new("schema.json", SkillResourceType.JsonSchema, "Input schema", "{}")
        };

        await store.SaveAsync(skill, resources);

        var result = await store.GetAsync("my-skill");
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Manifest);
        Assert.AreEqual(2, result.Manifest!.Count);
        Assert.AreEqual("script.py", result.Manifest[0].Filename);
        Assert.AreEqual(SkillResourceType.Python, result.Manifest[0].Type);
        Assert.AreEqual("A helper script", result.Manifest[0].Description);
        Assert.AreEqual("schema.json", result.Manifest[1].Filename);
        Assert.AreEqual(SkillResourceType.JsonSchema, result.Manifest[1].Type);
    }

    [TestMethod]
    public async Task SaveAsync_WithResources_ManifestPersistedAcrossInstances()
    {
        var store1 = CreateStore();
        var skill = MakeSkill("my-skill", "summary", "content");
        var resources = new List<SkillResourceInput>
        {
            new("helper.py", SkillResourceType.Python, "Helper", "# code")
        };

        await store1.SaveAsync(skill, resources);

        var store2 = CreateStore();
        var result = await store2.GetAsync("my-skill");
        Assert.IsNotNull(result?.Manifest);
        Assert.AreEqual(1, result!.Manifest!.Count);
        Assert.AreEqual("helper.py", result.Manifest[0].Filename);
    }

    [TestMethod]
    public async Task GetResourceAsync_ReturnsFileContent()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("my-skill", "summary", "content"),
            [new("script.py", SkillResourceType.Python, "desc", "print('hello')")]);

        var content = await store.GetResourceAsync("my-skill", "script.py");

        Assert.AreEqual("print('hello')", content);
    }

    [TestMethod]
    public async Task GetResourceAsync_NonexistentResource_ReturnsNull()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("my-skill", "summary", "content"));

        var content = await store.GetResourceAsync("my-skill", "ghost.py");

        Assert.IsNull(content);
    }

    [TestMethod]
    public async Task SaveAsync_WithResources_PrunesOrphanedFiles()
    {
        var store = CreateStore();
        var skill = MakeSkill("my-skill", "summary", "content");

        // First save with two resources
        await store.SaveAsync(skill,
        [
            new("a.py", SkillResourceType.Python, "desc", "code a"),
            new("b.py", SkillResourceType.Python, "desc", "code b")
        ]);

        // Re-save with only one resource — the other should be pruned
        await store.SaveAsync(skill with { },
        [
            new("a.py", SkillResourceType.Python, "desc", "code a v2")
        ]);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "my-skill.resources", "a.py")));
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "my-skill.resources", "b.py")));

        var manifest = (await store.GetAsync("my-skill"))?.Manifest;
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest!.Count);
        Assert.AreEqual("a.py", manifest[0].Filename);
    }

    [TestMethod]
    public async Task DeleteAsync_WithResources_DeletesResourceFolder()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("my-skill", "summary", "content"),
            [new("script.py", SkillResourceType.Python, "desc", "code")]);

        Assert.IsTrue(Directory.Exists(Path.Combine(_tempDir, "my-skill.resources")));

        await store.DeleteAsync("my-skill");

        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "my-skill.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDir, "my-skill.resources")));
    }

    [TestMethod]
    public async Task EnsureIndexAsync_SkipsResourceFolderJsonFiles()
    {
        // Write a skill and a resource JSON inside its resource folder
        var store1 = CreateStore();
        await store1.SaveAsync(MakeSkill("my-skill", "summary", "content"),
            [new("extra.json", SkillResourceType.JsonSchema, "desc", "{\"type\":\"object\"}")]);

        // A new store instance should load exactly one skill, not the resource JSON
        var store2 = CreateStore();
        var list = await store2.ListAsync();

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("my-skill", list[0].Name);
    }

    [TestMethod]
    public async Task SaveAsync_WithResources_SubcategorySkill_CreatesCorrectFolderLayout()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research/summarize", "summary", "content"),
            [new("helper.py", SkillResourceType.Python, "desc", "# code")]);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "research", "summarize.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "research", "summarize.resources", "helper.py")));
    }

    [TestMethod]
    public async Task SaveAsync_WithoutResources_PreservesExistingManifestAndFiles()
    {
        // Ensure re-saving a skill (markdown-only update) does not orphan resource files
        // or clear the manifest from the JSON.
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("my-skill", "summary", "v1 content"),
            [new("script.py", SkillResourceType.Python, "desc", "# v1")]);

        // Re-save without resources (markdown update only)
        await store.SaveAsync(MakeSkill("my-skill", "updated summary", "v2 content"));

        var result = await store.GetAsync("my-skill");
        Assert.IsNotNull(result);
        Assert.AreEqual("v2 content", result!.Content);

        // Manifest must still be present and file must still be on disk
        Assert.IsNotNull(result.Manifest);
        Assert.AreEqual(1, result.Manifest!.Count);
        Assert.AreEqual("script.py", result.Manifest[0].Filename);
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "my-skill.resources", "script.py")));
    }

    // ── Top-level and subcategory skill coexistence ───────────────────────────
    // The reserved ".resources" folder suffix means top-level skill "a" (which
    // stores its resources under "a.resources/") cannot collide with subcategory
    // skill "a/b" (which lives under "a/b.json"). Both layouts must coexist.

    [TestMethod]
    public async Task SaveAsync_TopLevelAndSubcategorySkillsCoexist()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research", "summary", "top-level"));
        await store.SaveAsync(MakeSkill("research/summarize", "summary", "subcategory"));

        Assert.IsNotNull(await store.GetAsync("research"));
        Assert.IsNotNull(await store.GetAsync("research/summarize"));
    }

    [TestMethod]
    public async Task SaveAsync_TopLevelSkillWithResources_DoesNotShadowSubcategory()
    {
        var store1 = CreateStore();
        await store1.SaveAsync(MakeSkill("research/summarize", "summary", "subcategory"));
        await store1.SaveAsync(MakeSkill("research", "summary", "top-level"),
            [new("helper.py", SkillResourceType.Python, "desc", "# code")]);

        // A new store must see both skills after re-indexing
        var store2 = CreateStore();
        var names = (await store2.ListAsync()).Select(s => s.Name).ToList();

        CollectionAssert.Contains(names, "research");
        CollectionAssert.Contains(names, "research/summarize");
    }

    [TestMethod]
    public async Task SaveAsync_NoConflict_SiblingSkillsSucceed()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("alpha", "summary", "content"));
        await store.SaveAsync(MakeSkill("alpha-extended", "summary", "content"));

        Assert.IsNotNull(await store.GetAsync("alpha"));
        Assert.IsNotNull(await store.GetAsync("alpha-extended"));
    }

    [TestMethod]
    public async Task SaveAsync_NoConflict_SiblingSubcategorySkillsSucceed()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research/plan", "summary", "content"));
        await store.SaveAsync(MakeSkill("research/summarize", "summary", "content"));

        Assert.IsNotNull(await store.GetAsync("research/plan"));
        Assert.IsNotNull(await store.GetAsync("research/summarize"));
    }

    [TestMethod]
    public void ValidateFilename_RejectsEmptyString()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateFilename(""));
    }

    [TestMethod]
    public void ValidateFilename_RejectsPathSeparator()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateFilename("sub/path.py"));
    }

    [TestMethod]
    public void ValidateFilename_RejectsBackslash()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateFilename(@"sub\path.py"));
    }

    [TestMethod]
    public void ValidateFilename_RejectsDotDot()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateFilename("../etc/passwd"));
    }

    [TestMethod]
    public void ValidateFilename_RejectsInvalidCharacters()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileSkillStore.ValidateFilename("my script .py"));
    }

    [TestMethod]
    public void ValidateFilename_AcceptsValidFilenames()
    {
        // Should not throw
        FileSkillStore.ValidateFilename("script.py");
        FileSkillStore.ValidateFilename("schema.json");
        FileSkillStore.ValidateFilename("my-helper_v2.py");
        FileSkillStore.ValidateFilename("automation.wisp");
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

    // ── AttachResourceAsync (Phase 2a: skill-asset promotion) ─────────────────

    [TestMethod]
    public async Task AttachResourceAsync_UnknownSkill_ReturnsFalse()
    {
        var store = CreateStore();
        var ok = await store.AttachResourceAsync(
            "nope",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{}"));
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task AttachResourceAsync_AddsManifestEntryAndWritesFile()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research/scan", "Scan", "# Scan"));

        var ok = await store.AttachResourceAsync(
            "research/scan",
            new SkillResourceInput(
                "fanout.json",
                SkillResourceType.Wisp,
                "Per-account fan-out",
                """{"description":"x","steps":[]}""",
                Provisional: true,
                VerifyHint: "exercises both accounts"));

        Assert.IsTrue(ok);

        // File on disk
        var resourcePath = Path.Combine(_tempDir, "research", "scan.resources", "fanout.json");
        Assert.IsTrue(File.Exists(resourcePath));

        // Manifest entry has the new fields
        var skill = await store.GetAsync("research/scan");
        Assert.IsNotNull(skill);
        Assert.IsNotNull(skill.Manifest);
        Assert.AreEqual(1, skill.Manifest!.Count);
        var entry = skill.Manifest[0];
        Assert.AreEqual("fanout.json", entry.Filename);
        Assert.IsTrue(entry.Provisional);
        Assert.IsNotNull(entry.CreatedAt);
        Assert.AreEqual("exercises both accounts", entry.VerifyHint);
        Assert.IsNotNull(entry.DefinitionHash);
        Assert.AreEqual(16, entry.DefinitionHash!.Length);
    }

    [TestMethod]
    public async Task AttachResourceAsync_ReplacesByFilename_PreservesOtherEntries()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("calendar/scan", "Scan", "# Scan"));

        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "first", "{\"v\":1}"));
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("b.py", SkillResourceType.Python, "second", "print('b')"));

        // Re-attach a.json with new content/description.
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "first-v2", "{\"v\":2}"));

        var skill = await store.GetAsync("calendar/scan");
        Assert.IsNotNull(skill?.Manifest);
        Assert.AreEqual(2, skill!.Manifest!.Count);

        var aEntry = skill.Manifest.Single(r => r.Filename == "a.json");
        Assert.AreEqual("first-v2", aEntry.Description);
        var aBody = await store.GetResourceAsync("calendar/scan", "a.json");
        Assert.AreEqual("{\"v\":2}", aBody);

        // b.py untouched
        var bEntry = skill.Manifest.Single(r => r.Filename == "b.py");
        Assert.AreEqual("second", bEntry.Description);
        var bBody = await store.GetResourceAsync("calendar/scan", "b.py");
        Assert.AreEqual("print('b')", bBody);
    }

    [TestMethod]
    public async Task AttachResourceAsync_PrebuiltManifestEntry_PersistsVerbatim()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("calendar/scan", "Scan", "# Scan"));

        var entry = new SkillResource(
            "fanout.json",
            SkillResourceType.Wisp,
            "Per-account fan-out",
            Provisional: false,
            CreatedAt: new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            VerifyHint: "exercises both accounts",
            DefinitionHash: "deadbeefcafef00d");
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("fanout.json", SkillResourceType.Wisp, "Per-account fan-out", "{}"),
            manifestEntry: entry);

        var skill = await store.GetAsync("calendar/scan");
        var saved = skill!.Manifest!.Single();
        Assert.IsFalse(saved.Provisional);
        Assert.AreEqual(entry.CreatedAt, saved.CreatedAt);
        Assert.AreEqual(entry.VerifyHint, saved.VerifyHint);
        Assert.AreEqual(entry.DefinitionHash, saved.DefinitionHash);
    }

    [TestMethod]
    public async Task RemoveResourceAsync_RemovesManifestEntryAndFile()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("research/scan", "Scan", "# Scan"));
        await store.AttachResourceAsync(
            "research/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{}"));
        await store.AttachResourceAsync(
            "research/scan",
            new SkillResourceInput("b.py", SkillResourceType.Python, "y", "print('y')"));

        var ok = await store.RemoveResourceAsync("research/scan", "a.json");
        Assert.IsTrue(ok);

        var skill = await store.GetAsync("research/scan");
        Assert.AreEqual(1, skill!.Manifest!.Count);
        Assert.AreEqual("b.py", skill.Manifest[0].Filename);

        var aPath = Path.Combine(_tempDir, "research", "scan.resources", "a.json");
        Assert.IsFalse(File.Exists(aPath));
    }

    [TestMethod]
    public async Task RemoveResourceAsync_UnknownEntry_ReturnsFalse()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("s", "summary", "content"));

        var ok = await store.RemoveResourceAsync("s", "missing.json");
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task UpdateResourceMetadataAsync_FlipsProvisionalAndPreservesContent()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("calendar/scan", "Scan", "# Scan"));
        await store.AttachResourceAsync(
            "calendar/scan",
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{\"v\":1}",
                Provisional: true,
                VerifyHint: "hint"));

        var initial = (await store.GetAsync("calendar/scan"))!.Manifest!.Single();
        Assert.IsTrue(initial.Provisional);

        // Validation pass would call this to flip Provisional=false while keeping VerifyHint.
        var ok = await store.UpdateResourceMetadataAsync(
            "calendar/scan",
            initial with { Provisional = false });
        Assert.IsTrue(ok);

        var after = (await store.GetAsync("calendar/scan"))!.Manifest!.Single();
        Assert.IsFalse(after.Provisional);
        Assert.AreEqual("hint", after.VerifyHint);  // preserved per user pref
        Assert.AreEqual(initial.CreatedAt, after.CreatedAt);
        Assert.AreEqual(initial.DefinitionHash, after.DefinitionHash);

        // File untouched
        var body = await store.GetResourceAsync("calendar/scan", "a.json");
        Assert.AreEqual("{\"v\":1}", body);
    }

    [TestMethod]
    public async Task UpdateResourceMetadataAsync_UnknownEntry_ReturnsFalse()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeSkill("s", "summary", "content"));

        var ok = await store.UpdateResourceMetadataAsync(
            "s",
            new SkillResource("missing.json", SkillResourceType.Wisp, "x"));
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task SaveAsync_2Arg_PreservesProvisionalAndVerifyHintOnInput()
    {
        var store = CreateStore();
        var skill = MakeSkill("calendar/scan", "Scan", "# Scan");
        await store.SaveAsync(skill, new[]
        {
            new SkillResourceInput("a.json", SkillResourceType.Wisp, "x", "{\"v\":1}",
                Provisional: true,
                VerifyHint: "verify-A")
        });

        var saved = await store.GetAsync("calendar/scan");
        var entry = saved!.Manifest!.Single();
        Assert.IsTrue(entry.Provisional);
        Assert.AreEqual("verify-A", entry.VerifyHint);
        Assert.IsNotNull(entry.DefinitionHash);
        Assert.IsNotNull(entry.CreatedAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileSkillStore CreateStore() =>
        new(Options.Create(new SkillOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileSkillStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static Skill MakeSkill(string name, string summary, string content) =>
        new(name, summary, content, DateTimeOffset.UtcNow);

    private static Skill MakeSkillWithSeeAlso(string name, string summary, string content, params string[] seeAlso) =>
        new(name, summary, content, DateTimeOffset.UtcNow, SeeAlso: seeAlso);
}
