using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="FileRulesStore"/> — in particular that <c>rules.md</c> is treated as a
/// document whose non-rule content survives a mutation, and that a file edited outside the
/// agent is not clobbered by the next write.
/// </summary>
[TestClass]
public sealed class FileRulesStoreTests
{
    private string _tempDir = null!;
    private string _rulesPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-rules-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _rulesPath = Path.Combine(_tempDir, "rules.md");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Basic behaviour ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task AddAsync_OnAFreshStore_CreatesTheFileWithAHeading()
    {
        var store = CreateStore();

        await store.AddAsync("Always respond in British English");

        var lines = await File.ReadAllLinesAsync(_rulesPath);
        Assert.AreEqual("# Active Rules", lines[0]);
        CollectionAssert.Contains(lines, "- Always respond in British English");
        CollectionAssert.AreEqual(new[] { "Always respond in British English" }, store.Rules.ToArray());
    }

    [TestMethod]
    public async Task AddAsync_DuplicateRule_IsIgnored()
    {
        var store = CreateStore();
        await store.AddAsync("Never use bullet points");

        await store.AddAsync("never use BULLET points");

        Assert.AreEqual(1, (await store.ListAsync()).Count);
    }

    [TestMethod]
    public async Task RemoveAsync_DropsOnlyThatRule()
    {
        var store = CreateStore();
        await store.AddAsync("Rule one");
        await store.AddAsync("Rule two");

        await store.RemoveAsync("rule one");

        CollectionAssert.AreEqual(new[] { "Rule two" }, (await store.ListAsync()).ToArray());
    }

    [TestMethod]
    public async Task RemoveAsync_UnknownRule_LeavesTheFileAlone()
    {
        var store = CreateStore();
        await store.AddAsync("Rule one");
        var before = await File.ReadAllTextAsync(_rulesPath);

        await store.RemoveAsync("no such rule");

        Assert.AreEqual(before, await File.ReadAllTextAsync(_rulesPath));
    }

    // ── Document round-trip ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AddAsync_PreservesHandAuthoredStructure()
    {
        // A rules.md someone wrote by hand: headings, prose, blank lines, a trailing note.
        // Regenerating the file from an extracted rule list destroyed all of it.
        await WriteRulesFileAsync(
            "# Active Rules",
            "",
            "These are enforced on every turn. Keep the list short.",
            "",
            "## Style",
            "",
            "- Always respond in British English",
            "- Never use bullet points in email drafts",
            "",
            "## Notes",
            "",
            "The style rules came from the 2026-03 review; ask before changing them.");

        var store = CreateStore();
        await store.AddAsync("Prefer prose to tables");

        var lines = await File.ReadAllLinesAsync(_rulesPath);
        CollectionAssert.AreEqual(
            new[]
            {
                "# Active Rules",
                "",
                "These are enforced on every turn. Keep the list short.",
                "",
                "## Style",
                "",
                "- Always respond in British English",
                "- Never use bullet points in email drafts",
                "- Prefer prose to tables",
                "",
                "## Notes",
                "",
                "The style rules came from the 2026-03 review; ask before changing them."
            },
            lines);
    }

    [TestMethod]
    public async Task RemoveAsync_PreservesHandAuthoredStructure()
    {
        await WriteRulesFileAsync(
            "# Active Rules",
            "",
            "Context for the reader.",
            "",
            "- Rule one",
            "- Rule two",
            "",
            "Trailing note.");

        var store = CreateStore();
        await store.RemoveAsync("Rule one");

        CollectionAssert.AreEqual(
            new[]
            {
                "# Active Rules",
                "",
                "Context for the reader.",
                "",
                "- Rule two",
                "",
                "Trailing note."
            },
            await File.ReadAllLinesAsync(_rulesPath));
    }

    [TestMethod]
    public async Task Load_IgnoresNonBulletLines()
    {
        await WriteRulesFileAsync(
            "# Active Rules",
            "",
            "Some prose that is not a rule.",
            "- An actual rule");

        var store = CreateStore();

        CollectionAssert.AreEqual(new[] { "An actual rule" }, store.Rules.ToArray());
    }

    [TestMethod]
    public async Task AddAsync_PreservesAsteriskBulletsAlreadyInTheFile()
    {
        await WriteRulesFileAsync("* Written with an asterisk");

        var store = CreateStore();
        await store.AddAsync("Added by the agent");

        var lines = await File.ReadAllLinesAsync(_rulesPath);
        CollectionAssert.AreEqual(
            new[] { "* Written with an asterisk", "- Added by the agent" },
            lines);
    }

    // ── Re-read on mutation ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AddAsync_DoesNotClobberRulesAddedToTheFileSinceStartup()
    {
        // The store has no watcher, so a rules.md pushed to a running pod would previously be
        // overwritten by the next add from the startup-loaded list.
        var store = CreateStore();
        await store.AddAsync("Agent rule");

        await File.AppendAllLinesAsync(_rulesPath, ["- Rule added out of band"]);

        await store.AddAsync("Another agent rule");

        var rules = await store.ListAsync();
        CollectionAssert.AreEquivalent(
            new[] { "Agent rule", "Rule added out of band", "Another agent rule" },
            rules.ToArray());
    }

    [TestMethod]
    public async Task ListAsync_ReflectsAnOutOfBandEdit()
    {
        var store = CreateStore();
        await store.AddAsync("Agent rule");

        await File.AppendAllLinesAsync(_rulesPath, ["- Rule added out of band"]);

        CollectionAssert.Contains((await store.ListAsync()).ToArray(), "Rule added out of band");
    }

    // ── EditAsync ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditAsync_ChangesTheRuleInPlace()
    {
        await WriteRulesFileAsync(
            "# Active Rules",
            "",
            "- Never use bullet points",
            "- Always sign off with a name");

        var store = CreateStore();
        var result = await store.EditAsync("Never use bullet points", "Never use bullet points in email drafts");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.ReplacementCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "# Active Rules",
                "",
                "- Never use bullet points in email drafts",
                "- Always sign off with a name"
            },
            await File.ReadAllLinesAsync(_rulesPath));
    }

    [TestMethod]
    public async Task EditAsync_KeepsTheRuleInItsOriginalPosition()
    {
        await WriteRulesFileAsync("- First", "- Second", "- Third");

        var store = CreateStore();
        await store.EditAsync("Second", "Middle");

        CollectionAssert.AreEqual(
            new[] { "First", "Middle", "Third" },
            (await store.ListAsync()).ToArray());
    }

    [TestMethod]
    public async Task EditAsync_TextNotFound_Refuses()
    {
        await WriteRulesFileAsync("- Never use bullet points");
        var store = CreateStore();

        var result = await store.EditAsync("tables", "charts");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "not found");
    }

    [TestMethod]
    public async Task EditAsync_AmbiguousAcrossRules_Refuses_AndWritesNothing()
    {
        await WriteRulesFileAsync("- Prefer prose", "- Prefer prose over tables");
        var store = CreateStore();
        var before = await File.ReadAllTextAsync(_rulesPath);

        var result = await store.EditAsync("Prefer prose", "Prefer bullets");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "occurs 2 times");
        Assert.AreEqual(before, await File.ReadAllTextAsync(_rulesPath));
    }

    [TestMethod]
    public async Task EditAsync_ReplaceAll_ChangesEveryMatchingRule()
    {
        await WriteRulesFileAsync("- Prefer prose", "- Prefer prose over tables");
        var store = CreateStore();

        var result = await store.EditAsync("Prefer prose", "Prefer bullets", replaceAll: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.ReplacementCount);
        CollectionAssert.AreEqual(
            new[] { "Prefer bullets", "Prefer bullets over tables" },
            (await store.ListAsync()).ToArray());
    }

    [TestMethod]
    public async Task EditAsync_DoesNotMatchAgainstNonRuleLines()
    {
        await WriteRulesFileAsync(
            "# Active Rules",
            "",
            "The word chicago appears in this note.",
            "- An unrelated rule");

        var store = CreateStore();
        var result = await store.EditAsync("chicago", "denver");

        Assert.IsFalse(result.IsSuccess, "Prose is not a rule and must not be edited by edit_rule.");
    }

    [TestMethod]
    public async Task EditAsync_EmptyOldText_Refuses()
    {
        await WriteRulesFileAsync("- A rule");
        var store = CreateStore();

        var result = await store.EditAsync("", "something");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "must not be empty");
    }

    [TestMethod]
    public async Task EditAsync_IdenticalOldAndNew_Refuses()
    {
        await WriteRulesFileAsync("- A rule");
        var store = CreateStore();

        var result = await store.EditAsync("A rule", "A rule");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "identical");
    }

    [TestMethod]
    public async Task EditAsync_RefreshesTheSynchronousRulesSnapshot()
    {
        await WriteRulesFileAsync("- Never use bullet points");
        var store = CreateStore();

        await store.EditAsync("bullet points", "tables");

        CollectionAssert.AreEqual(new[] { "Never use tables" }, store.Rules.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileRulesStore CreateStore() =>
        new(Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<FileRulesStore>.Instance);

    private Task WriteRulesFileAsync(params string[] lines) =>
        File.WriteAllLinesAsync(_rulesPath, lines);
}
