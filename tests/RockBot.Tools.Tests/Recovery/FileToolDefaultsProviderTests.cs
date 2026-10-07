using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools.Mcp;
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
    public async Task ResolveAsync_ArrayValue_RejectedAtLoadTime()
    {
        // Amendment 1 step 5: array entries are rejected at load time and
        // never enter the in-memory map. CanResolve returns false (no entry
        // to match against) rather than the resolve path returning null.
        // This makes the misconfiguration visible in logs.
        await WriteServerFileAsync("calendar-mcp", """
            [
              { "providerName": "AccountIds", "field": "accountId", "value": ["a@x", "b@x"] },
              { "providerName": "TimeZone", "field": "timeZone", "value": "UTC" }
            ]
            """);

        using var provider = NewProvider();

        // The array entry was dropped; the scalar entry alongside it still works.
        Assert.IsFalse(provider.CanResolve("calendar-mcp", "get_calendar_events", "accountId"));
        Assert.IsTrue(provider.CanResolve("calendar-mcp", "get_calendar_events", "timeZone"));

        var ctx = new ResolveContext("calendar-mcp", "get_calendar_events", "accountId",
            new Dictionary<string, object?>());
        Assert.IsNull(await provider.ResolveAsync(ctx, CancellationToken.None));
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

    // ── Stale entries (#615) ─────────────────────────────────────────────────

    private const string EventsSchema = """{"type":"object","properties":{"timeZone":{"type":"string"},"start":{"type":"string"}}}""";

    [TestMethod]
    public async Task ResolveAsync_FieldNoLongerInSchema_IsNotInjected_AndWarnsOnce()
    {
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "TimeZone", "field": "tz", "value": "America/Chicago" } ]
            """);
        var logger = new CountingLogger<FileToolDefaultsProvider>();
        using var provider = NewProvider(SchemaCache(EventsSchema), logger);

        var ctx = new ResolveContext("calendar-mcp", "get_calendar_events", "tz", new Dictionary<string, object?>());
        Assert.IsNull(await provider.ResolveAsync(ctx, CancellationToken.None));
        Assert.IsNull(await provider.ResolveAsync(ctx, CancellationToken.None));

        Assert.AreEqual(1, logger.Warnings, "one warning per server/tool/field, not one per call");
    }

    [TestMethod]
    public async Task ResolveAsync_FieldStillInSchema_IsInjected()
    {
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "TimeZone", "field": "timeZone", "value": "America/Chicago" } ]
            """);
        using var provider = NewProvider(SchemaCache(EventsSchema), new CountingLogger<FileToolDefaultsProvider>());

        var resolved = await provider.ResolveAsync(
            new ResolveContext("calendar-mcp", "get_calendar_events", "timeZone", new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.AreEqual("America/Chicago", resolved?.Value);
    }

    [TestMethod]
    public async Task ResolveAsync_SchemaUnavailable_ResolvesAsBefore()
    {
        await WriteServerFileAsync("calendar-mcp", """
            [ { "providerName": "TimeZone", "field": "tz", "value": "America/Chicago" } ]
            """);
        using var provider = NewProvider(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new CountingLogger<FileToolDefaultsProvider>());

        var resolved = await provider.ResolveAsync(
            new ResolveContext("calendar-mcp", "get_calendar_events", "tz", new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.AreEqual("America/Chicago", resolved?.Value);
    }

    [TestMethod]
    [DataRow("""{"type":"object","properties":{"a":{}}}""", "a", false)]
    [DataRow("""{"type":"object","properties":{"A":{}}}""", "a", false)]
    [DataRow("""{"type":"object","properties":{"a":{}}}""", "b", true)]
    [DataRow("""{"type":"object","properties":{}}""", "b", true)]
    [DataRow("""{"type":"object"}""", "b", null)]
    [DataRow("not json", "b", null)]
    [DataRow("", "b", null)]
    public void IsMissingFromSchema_IsTriState(string schema, string field, bool? expected)
    {
        Assert.AreEqual(expected, FileToolDefaultsProvider.IsMissingFromSchema(schema, field));
    }

    private static ToolSchemaCache SchemaCache(string schema) =>
        new((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(
            [new McpToolDefinition { Name = "get_calendar_events", Description = "d", ParametersSchema = schema }]));

    private FileToolDefaultsProvider NewProvider(ToolSchemaCache schemas, ILogger<FileToolDefaultsProvider> logger) =>
        new(Options.Create(new AgentProfileOptions { BasePath = _tempDir }), logger, schemas);

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
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
