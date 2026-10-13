using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public class SkillBodyApplierTests
{
    [TestMethod]
    public async Task Apply_AppendOp_AppendsTextWithBlankLineSeparator()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "summary", "Original line.", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/foo", "ops": [ { "op": "append", "text": "New trailing line." } ] }
            """);

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);

        var saved = await store.GetAsync("calendar/foo");
        Assert.IsNotNull(saved);
        StringAssert.Contains(saved!.Content, "Original line.");
        StringAssert.Contains(saved.Content, "New trailing line.");
        Assert.IsNotNull(outcome.Revert);
    }

    // -- Code-owned guides go to their companion (#699) --------------------------

    private static async Task<(InMemorySkillStore Store, Skill Guide)> StoreWithGuide()
    {
        var store = new InMemorySkillStore();
        var guide = new Skill("mcp", "summary", "# MCP guide\n\n## Argument tips\n\nGuide text.\n",
            DateTimeOffset.UtcNow, CodeOwned: true);
        await store.SaveAsync(guide);
        return (store, guide);
    }

    [TestMethod]
    public async Task Apply_CodeOwnedGuide_AppendsToANewCompanion_AndLeavesTheGuide()
    {
        var (store, guide) = await StoreWithGuide();

        var outcome = await NewApplier(store).ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "append", "text": "Pass the server explicitly." } ] }
            """), CancellationToken.None);

        Assert.AreSame(guide, await store.GetAsync("mcp"));
        var companion = (await store.GetAsync("mcp-learned"))!;
        Assert.IsNotNull(companion);
        Assert.IsFalse(companion.CodeOwned);
        CollectionAssert.AreEqual(new[] { "mcp" }, companion.SeeAlso!.ToArray());
        StringAssert.Contains(companion.Content, "get_tool_guide(\"mcp\")");
        StringAssert.Contains(companion.Content, "Pass the server explicitly.");
        Assert.AreEqual("mcp-learned", outcome.AppliedDiff.GetProperty("skill").GetString());
        Assert.AreEqual("mcp", outcome.AppliedDiff.GetProperty("redirectedFrom").GetString());
    }

    [TestMethod]
    public async Task Apply_CodeOwnedGuide_ReplaceSection_AddsItToTheCompanion_ThenUpdatesItInPlace()
    {
        var (store, guide) = await StoreWithGuide();
        var applier = NewApplier(store);

        await applier.ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "replaceSection", "header": "## Argument tips", "text": "Always pass timeZone." } ] }
            """), CancellationToken.None);
        await applier.ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "replaceSection", "header": "## Argument tips", "text": "Pass timeZone as IANA." } ] }
            """), CancellationToken.None);

        Assert.AreSame(guide, await store.GetAsync("mcp"));
        var content = (await store.GetAsync("mcp-learned"))!.Content;
        Assert.AreEqual(1, content.Split("## Argument tips").Length - 1, "the second ticket replaces, not duplicates");
        StringAssert.Contains(content, "## Argument tips\n\nPass timeZone as IANA.");
        Assert.IsFalse(content.Contains("Always pass timeZone."));
    }

    [TestMethod]
    public async Task Apply_CodeOwnedGuide_DeleteSectionOfTheGuide_FailsAndWritesNothing()
    {
        var (store, guide) = await StoreWithGuide();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NewApplier(store).ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "deleteSection", "header": "## Argument tips" } ] }
            """), CancellationToken.None));

        StringAssert.Contains(ex.Message, "maintained in code");
        Assert.AreSame(guide, await store.GetAsync("mcp"));
        Assert.IsNull(await store.GetAsync("mcp-learned"));
    }

    [TestMethod]
    public async Task Revert_CodeOwnedGuide_DeletesACompanionItCreated()
    {
        var (store, _) = await StoreWithGuide();

        var outcome = await NewApplier(store).ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "append", "text": "Lesson." } ] }
            """), CancellationToken.None);
        await outcome.Revert!(CancellationToken.None);

        Assert.IsNull(await store.GetAsync("mcp-learned"));
        Assert.IsNotNull(await store.GetAsync("mcp"));
    }

    [TestMethod]
    public async Task Revert_CodeOwnedGuide_RestoresAnExistingCompanion()
    {
        var (store, _) = await StoreWithGuide();
        await store.SaveAsync(new Skill("mcp-learned", "s", "# Earlier lessons", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(-1), SeeAlso: ["mcp"]));

        var outcome = await NewApplier(store).ApplyAsync(NewTicket("""
            { "skill": "mcp", "ops": [ { "op": "append", "text": "Lesson." } ] }
            """), CancellationToken.None);
        StringAssert.Contains((await store.GetAsync("mcp-learned"))!.Content, "Lesson.");

        await outcome.Revert!(CancellationToken.None);

        Assert.AreEqual("# Earlier lessons", (await store.GetAsync("mcp-learned"))!.Content);
    }

    [TestMethod]
    public async Task Apply_ReplaceSection_RewritesIdentifiedSection()
    {
        var initial =
            "# Title\n\n" +
            "Intro paragraph.\n\n" +
            "## Argument tips\n\n" +
            "Old guidance.\n\n" +
            "## Other section\n\n" +
            "Other content.\n";
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "summary", initial, DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/foo", "ops": [ { "op": "replaceSection", "header": "## Argument tips", "text": "New guidance: always pass timeZone." } ] }
            """);

        await applier.ApplyAsync(ticket, CancellationToken.None);

        var saved = await store.GetAsync("calendar/foo");
        StringAssert.Contains(saved!.Content, "## Argument tips");
        StringAssert.Contains(saved.Content, "New guidance: always pass timeZone.");
        Assert.IsFalse(saved.Content.Contains("Old guidance"), "Old section content must be gone.");
        StringAssert.Contains(saved.Content, "## Other section");
        StringAssert.Contains(saved.Content, "Other content.");
    }

    [TestMethod]
    public async Task Apply_DeleteSection_RemovesIdentifiedSection()
    {
        var initial =
            "## Keep\n\nKeeper.\n\n" +
            "## Drop\n\nDroppable content.\n\n" +
            "## Also keep\n\nAlso keeper.\n";
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "summary", initial, DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/foo", "ops": [ { "op": "deleteSection", "header": "## Drop" } ] }
            """);

        await applier.ApplyAsync(ticket, CancellationToken.None);

        var saved = await store.GetAsync("calendar/foo");
        Assert.IsFalse(saved!.Content.Contains("Drop"), "Section heading must be gone.");
        Assert.IsFalse(saved.Content.Contains("Droppable content"), "Section body must be gone.");
        StringAssert.Contains(saved.Content, "Keeper.");
        StringAssert.Contains(saved.Content, "Also keeper.");
    }

    [TestMethod]
    public async Task Apply_ReplaceSection_MissingHeader_Throws()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "s", "## Other\n\ncontent.\n", DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/foo", "ops": [ { "op": "replaceSection", "header": "## Missing", "text": "..." } ] }
            """);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_RevertCallback_RestoresPriorContent()
    {
        var pre = "Original content.\n";
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "s", pre, DateTimeOffset.UtcNow));

        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/foo", "ops": [ { "op": "append", "text": "New trailing line." } ] }
            """);

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);
        Assert.IsNotNull(outcome.Revert);

        await outcome.Revert!(CancellationToken.None);

        var reverted = await store.GetAsync("calendar/foo");
        Assert.AreEqual(pre, reverted!.Content);
    }

    [TestMethod]
    public async Task Apply_MissingSkill_Throws()
    {
        var store = new InMemorySkillStore();
        var applier = NewApplier(store);
        var ticket = NewTicket("""
            { "skill": "calendar/missing", "ops": [ { "op": "append", "text": "x" } ] }
            """);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_NoOps_Throws()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "s", "x", DateTimeOffset.UtcNow));
        var applier = NewApplier(store);
        var ticket = NewTicket("""{ "skill": "calendar/foo", "ops": [] }""");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_RecordsAppliedDiffWithHashesAndOps()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "s", "before.\n", DateTimeOffset.UtcNow));
        var applier = NewApplier(store);
        var ticket = NewTicket("""{ "skill": "calendar/foo", "ops": [ { "op": "append", "text": "after." } ] }""");

        var outcome = await applier.ApplyAsync(ticket, CancellationToken.None);

        var diff = outcome.AppliedDiff;
        Assert.AreEqual("calendar/foo", diff.GetProperty("skill").GetString());
        Assert.IsTrue(diff.TryGetProperty("preHash", out var preHash));
        Assert.IsTrue(diff.TryGetProperty("postHash", out var postHash));
        Assert.AreNotEqual(preHash.GetString(), postHash.GetString());
    }

    private static SkillBodyApplier NewApplier(ISkillStore store) =>
        new(store, NullLogger<SkillBodyApplier>.Instance);

    internal sealed class InMemorySkillStore : ISkillStore
    {
        private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);

        public Task SaveAsync(Skill skill)
        {
            _skills[skill.Name] = skill;
            return Task.CompletedTask;
        }

        public Task<Skill?> GetAsync(string name) =>
            Task.FromResult(_skills.TryGetValue(name, out var s) ? s : null);

        public Task<IReadOnlyList<Skill>> ListAsync() =>
            Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());

        public Task DeleteAsync(string name)
        {
            _skills.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private static RepairTicket NewTicket(string changeJson)
    {
        var change = JsonDocument.Parse(changeJson).RootElement;
        return new RepairTicket(
            Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.SkillBody,
            Change: change,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    // -- Accepted spellings of the skill field ---------------------------------

    [TestMethod]
    public void ResolvedSkill_PrefersSkill_ThenSkillName_ThenName()
    {
        // The creation directive documented this target's ops array but never named the
        // identifier field, while spelling it out for every other target. Models filled the gap
        // with the obvious synonyms and every such ticket failed validation -- on a live agent,
        // the same ticket failed 117 times across two months.
        Assert.AreEqual("a", new SkillBodyApplier.SkillBodyChange { Skill = "a", SkillName = "b", Name = "c" }.ResolvedSkill);
        Assert.AreEqual("b", new SkillBodyApplier.SkillBodyChange { SkillName = "b", Name = "c" }.ResolvedSkill);
        Assert.AreEqual("c", new SkillBodyApplier.SkillBodyChange { Name = "c" }.ResolvedSkill);
    }

    [TestMethod]
    public void ResolvedSkill_TreatsBlankAsAbsent()
    {
        Assert.AreEqual("real", new SkillBodyApplier.SkillBodyChange { Skill = "   ", SkillName = "real" }.ResolvedSkill);
        Assert.IsNull(new SkillBodyApplier.SkillBodyChange { Skill = " ", SkillName = "", Name = null }.ResolvedSkill);
    }
}
