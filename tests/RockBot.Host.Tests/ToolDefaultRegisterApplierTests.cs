using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class ToolDefaultRegisterApplierTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-tooldefaults-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task Apply_WritesJsonArrayToServerFile()
    {
        var applier = NewApplier();
        var ticket = NewTicket("""
            { "server": "calendar-mcp", "providerName": "TimeZoneFromConfig", "field": "timeZone", "tool": "get_calendar_events", "value": "America/Chicago" }
            """);

        await applier.ApplyAsync(ticket, CancellationToken.None);

        var path = Path.Combine(_tempDir, "tool-defaults", "calendar-mcp.json");
        Assert.IsTrue(File.Exists(path));
        var arr = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        Assert.AreEqual(JsonValueKind.Array, arr.ValueKind);
        Assert.AreEqual(1, arr.GetArrayLength());
        Assert.AreEqual("TimeZoneFromConfig", arr[0].GetProperty("providerName").GetString());
        Assert.AreEqual("America/Chicago", arr[0].GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task Apply_SameProviderName_OverwritesEntry_DoesNotDuplicate()
    {
        var applier = NewApplier();
        var t1 = NewTicket("""
            { "server": "calendar-mcp", "providerName": "TimeZoneFromConfig", "field": "timeZone", "value": "America/Chicago" }
            """);
        var t2 = NewTicket("""
            { "server": "calendar-mcp", "providerName": "TimeZoneFromConfig", "field": "timeZone", "value": "America/Los_Angeles" }
            """);

        await applier.ApplyAsync(t1, CancellationToken.None);
        await applier.ApplyAsync(t2, CancellationToken.None);

        var path = Path.Combine(_tempDir, "tool-defaults", "calendar-mcp.json");
        var arr = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        Assert.AreEqual(1, arr.GetArrayLength());
        Assert.AreEqual("America/Los_Angeles", arr[0].GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task Apply_DifferentProviderName_AppendsAdditionalEntry()
    {
        var applier = NewApplier();
        await applier.ApplyAsync(NewTicket("""
            { "server": "calendar-mcp", "providerName": "TimeZone", "field": "timeZone", "value": "America/Chicago" }
            """), CancellationToken.None);
        await applier.ApplyAsync(NewTicket("""
            { "server": "calendar-mcp", "providerName": "AccountId", "field": "accountId", "value": "primary" }
            """), CancellationToken.None);

        var path = Path.Combine(_tempDir, "tool-defaults", "calendar-mcp.json");
        var arr = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        Assert.AreEqual(2, arr.GetArrayLength());
    }

    [TestMethod]
    public async Task Apply_MissingField_Throws()
    {
        var applier = NewApplier();
        var ticket = NewTicket("""{ "server": "x", "providerName": "p", "value": "v" }""");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => applier.ApplyAsync(ticket, CancellationToken.None));
    }

    private ToolDefaultRegisterApplier NewApplier() =>
        new(
            Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<ToolDefaultRegisterApplier>.Instance);

    private static RepairTicket NewTicket(string changeJson) =>
        new(
            Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.ToolDefaultRegister,
            Change: JsonDocument.Parse(changeJson).RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
