using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class PromptBuilderHintApplierTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-prompthints-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task Apply_NewCategoryFile_CreatesAndAppendsHint()
    {
        var applier = NewApplier();
        var ticket = NewTicket("""
            { "category": "patrol", "hintId": "calendar-fanout", "text": "Fan out by accountId when scanning calendars." }
            """);

        await applier.ApplyAsync(ticket, CancellationToken.None);

        var path = Path.Combine(_tempDir, "prompt-hints", "patrol.md");
        var content = await File.ReadAllTextAsync(path);
        StringAssert.Contains(content, "<!-- hint:calendar-fanout -->");
        StringAssert.Contains(content, "Fan out by accountId");
        StringAssert.Contains(content, "<!-- /hint:calendar-fanout -->");
    }

    [TestMethod]
    public async Task Apply_SameHintId_ReplacesInPlace_DoesNotDuplicate()
    {
        var applier = NewApplier();
        await applier.ApplyAsync(NewTicket("""
            { "category": "patrol", "hintId": "x", "text": "first text" }
            """), CancellationToken.None);
        await applier.ApplyAsync(NewTicket("""
            { "category": "patrol", "hintId": "x", "text": "replaced text" }
            """), CancellationToken.None);

        var path = Path.Combine(_tempDir, "prompt-hints", "patrol.md");
        var content = await File.ReadAllTextAsync(path);

        Assert.IsFalse(content.Contains("first text"), "Old hint body must be gone.");
        StringAssert.Contains(content, "replaced text");

        // Only one open marker for this hintId.
        var openCount = content.Split("<!-- hint:x -->", StringSplitOptions.None).Length - 1;
        Assert.AreEqual(1, openCount);
    }

    [TestMethod]
    public async Task Apply_DifferentHintIds_BothAppend()
    {
        var applier = NewApplier();
        await applier.ApplyAsync(NewTicket("""
            { "category": "patrol", "hintId": "a", "text": "alpha" }
            """), CancellationToken.None);
        await applier.ApplyAsync(NewTicket("""
            { "category": "patrol", "hintId": "b", "text": "beta" }
            """), CancellationToken.None);

        var path = Path.Combine(_tempDir, "prompt-hints", "patrol.md");
        var content = await File.ReadAllTextAsync(path);
        StringAssert.Contains(content, "<!-- hint:a -->");
        StringAssert.Contains(content, "<!-- hint:b -->");
        StringAssert.Contains(content, "alpha");
        StringAssert.Contains(content, "beta");
    }

    [TestMethod]
    public async Task Apply_RejectsUnsafeCategoryName()
    {
        var applier = NewApplier();
        var ticket = NewTicket("""
            { "category": "../escape", "hintId": "x", "text": "y" }
            """);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    [TestMethod]
    public async Task Apply_MissingFields_Throws()
    {
        var applier = NewApplier();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(
                NewTicket("""{ "hintId": "x", "text": "y" }"""),
                CancellationToken.None));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(
                NewTicket("""{ "category": "x", "text": "y" }"""),
                CancellationToken.None));
    }

    private PromptBuilderHintApplier NewApplier() =>
        new(
            Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<PromptBuilderHintApplier>.Instance);

    private static RepairTicket NewTicket(string changeJson) =>
        new(
            Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.PromptBuilderHint,
            Change: JsonDocument.Parse(changeJson).RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
