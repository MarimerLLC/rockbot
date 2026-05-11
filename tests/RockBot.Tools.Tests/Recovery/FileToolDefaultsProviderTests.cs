using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools.Mcp.Recovery;
using RockBot.Tools.Mcp.Recovery.Providers;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class FileToolDefaultsProviderTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-filetooldefaults-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "tool-defaults"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* watcher may briefly hold a handle */ }
        }
    }

    [TestMethod]
    public async Task CanResolve_AfterFileLoad_ReturnsTrueForConfiguredField()
    {
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "TimeZone", "field": "timeZone", "value": "America/Chicago" } ]
            """);

        using var provider = NewProvider();

        Assert.IsTrue(provider.CanResolve("calendar-mcp", "get_calendar_events", "timeZone"));
        Assert.IsFalse(provider.CanResolve("calendar-mcp", "get_calendar_events", "otherField"));
        Assert.IsFalse(provider.CanResolve("other-server", "x", "timeZone"));
    }

    [TestMethod]
    public async Task ResolveAsync_StringValue_ReturnsScalar()
    {
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "TimeZone", "field": "timeZone", "value": "America/Chicago" } ]
            """);

        using var provider = NewProvider();

        var ctx = new ResolveContext("calendar-mcp", "get_calendar_events", "timeZone",
            new Dictionary<string, object?>());
        var resolved = await provider.ResolveAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(resolved);
        Assert.AreEqual("America/Chicago", resolved!.Value);
    }

    [TestMethod]
    public async Task ResolveAsync_ArrayValue_DoesNotResolve()
    {
        // Amendment 1: arrays no longer produce fan-out defaults. The provider
        // declines to resolve so the recovery executor surfaces a single
        // schema error to the LLM instead of issuing N parallel calls.
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "AccountIds", "field": "accountId", "value": ["a@x", "b@x"] } ]
            """);

        using var provider = NewProvider();

        var ctx = new ResolveContext("calendar-mcp", "get_calendar_events", "accountId",
            new Dictionary<string, object?>());
        var resolved = await provider.ResolveAsync(ctx, CancellationToken.None);

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public async Task ToolField_ScopesEntryToSpecificTool()
    {
        await WriteServerFileAsync("svr", """
            [ { "providerName": "Scoped", "field": "f", "tool": "tool-a", "value": "for-a" } ]
            """);

        using var provider = NewProvider();

        Assert.IsTrue(provider.CanResolve("svr", "tool-a", "f"));
        Assert.IsFalse(provider.CanResolve("svr", "tool-b", "f"));
    }

    [TestMethod]
    public async Task HotReload_PicksUpFileChanges()
    {
        await WriteServerFileAsync("svr", """[]""");
        using var provider = NewProvider();

        Assert.IsFalse(provider.CanResolve("svr", "tool", "f"));

        await WriteServerFileAsync("svr", """
            [ { "providerName": "P", "field": "f", "value": "v" } ]
            """);

        // FileSystemWatcher is async; poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !provider.CanResolve("svr", "tool", "f"))
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(provider.CanResolve("svr", "tool", "f"),
            "hot reload should have picked up the new entry");
    }

    [TestMethod]
    public async Task MalformedJsonFile_DoesNotThrow_ServerSimplyMissing()
    {
        await WriteServerFileAsync("svr", "{not-json{");

        using var provider = NewProvider();

        Assert.IsFalse(provider.CanResolve("svr", "tool", "f"));
    }

    private async Task WriteServerFileAsync(string server, string content)
    {
        var path = Path.Combine(_tempDir, "tool-defaults", server + ".json");
        await File.WriteAllTextAsync(path, content);
    }

    private FileToolDefaultsProvider NewProvider() =>
        new(
            Options.Create(new AgentProfileOptions { BasePath = _tempDir }),
            NullLogger<FileToolDefaultsProvider>.Instance);
}
