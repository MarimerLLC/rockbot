using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class McpPreflightRecoveryTests
{
    [TestMethod]
    public async Task NoMissingFields_ReturnsEmptyResult()
    {
        var recovery = new McpPreflightRecovery(
            providers: [],
            enricher: null,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        var result = await recovery.TryRecoverAsync(
            "srv", "do", missingFields: [],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: null, CancellationToken.None);

        Assert.AreEqual(0, result.FilledDefaults.Count);
        Assert.AreEqual(0, result.UnresolvedFields.Count);
        Assert.IsNull(result.EnrichedErrorContext);
    }

    [TestMethod]
    public async Task EnvironmentalProvider_FillsField_SilentlyAndDropsFromUnresolved()
    {
        var provider = new StubProvider("timeZone", "America/Chicago");
        var recovery = new McpPreflightRecovery(
            providers: [provider],
            enricher: null,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        var result = await recovery.TryRecoverAsync(
            "calendar-mcp", "get_calendar_events",
            missingFields: ["timeZone"],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: null, CancellationToken.None);

        Assert.AreEqual(1, result.FilledDefaults.Count);
        Assert.AreEqual("America/Chicago", result.FilledDefaults["timeZone"]);
        Assert.AreEqual(0, result.UnresolvedFields.Count);
        Assert.IsNull(result.EnrichedErrorContext);
    }

    [TestMethod]
    public async Task UnfilledFields_AreEnriched()
    {
        // No provider for emailId — recovery should mark it unresolved and ask
        // the enricher to build context for it.
        var schemasByServer = new Dictionary<string, IReadOnlyList<McpToolDefinition>>
        {
            ["calendar-mcp"] = new[]
            {
                new McpToolDefinition
                {
                    Name = "get_email_details",
                    Description = "Fetch full email content. Obtain emailId from search_emails results.",
                    ParametersSchema = """{"type":"object","properties":{"emailId":{"type":"string","description":"Email id from a search result"}},"required":["emailId"]}"""
                }
            }
        };
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((server, _) =>
                Task.FromResult<IReadOnlyList<McpToolDefinition>?>(
                    schemasByServer.TryGetValue(server, out var v) ? v : null)),
            new EmptyToolCallLog());

        var recovery = new McpPreflightRecovery(
            providers: [],
            enricher: enricher,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        var result = await recovery.TryRecoverAsync(
            "calendar-mcp", "get_email_details",
            missingFields: ["emailId"],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: null, CancellationToken.None);

        Assert.AreEqual(0, result.FilledDefaults.Count);
        CollectionAssert.AreEqual(new[] { "emailId" }, result.UnresolvedFields.ToArray());
        Assert.IsNotNull(result.EnrichedErrorContext);
        StringAssert.Contains(result.EnrichedErrorContext!, "Email id from a search result");
        StringAssert.Contains(result.EnrichedErrorContext!, "search_emails");
    }

    [TestMethod]
    public async Task PartialFill_OneFilledOneEnriched()
    {
        var schemasByServer = new Dictionary<string, IReadOnlyList<McpToolDefinition>>
        {
            ["calendar-mcp"] = new[]
            {
                new McpToolDefinition
                {
                    Name = "get_calendar_events",
                    Description = "List events. accountId required. timeZone required.",
                    ParametersSchema = """{"type":"object","properties":{"accountId":{"type":"string"},"timeZone":{"type":"string","description":"IANA tz"}},"required":["accountId","timeZone"]}"""
                }
            }
        };
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((server, _) =>
                Task.FromResult<IReadOnlyList<McpToolDefinition>?>(
                    schemasByServer.TryGetValue(server, out var v) ? v : null)),
            new EmptyToolCallLog());

        var provider = new StubProvider("timeZone", "America/Chicago");
        var recovery = new McpPreflightRecovery(
            providers: [provider],
            enricher: enricher,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        var result = await recovery.TryRecoverAsync(
            "calendar-mcp", "get_calendar_events",
            missingFields: ["accountId", "timeZone"],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: null, CancellationToken.None);

        Assert.AreEqual(1, result.FilledDefaults.Count);
        Assert.AreEqual("America/Chicago", result.FilledDefaults["timeZone"]);
        CollectionAssert.AreEqual(new[] { "accountId" }, result.UnresolvedFields.ToArray());
        Assert.IsNotNull(result.EnrichedErrorContext);
        StringAssert.Contains(result.EnrichedErrorContext!, "accountId");
    }

    [TestMethod]
    public async Task ProviderThrows_ContinuesToNextProvider()
    {
        var throwing = new ThrowingProvider("timeZone");
        var working = new StubProvider("timeZone", "UTC");

        var recovery = new McpPreflightRecovery(
            providers: [throwing, working],
            enricher: null,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        var result = await recovery.TryRecoverAsync(
            "srv", "tool", missingFields: ["timeZone"],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: null, CancellationToken.None);

        Assert.AreEqual("UTC", result.FilledDefaults["timeZone"]);
    }

    [TestMethod]
    public async Task ParentSessionId_FlowsToEnricher()
    {
        var captured = new List<string?>();
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new CapturingToolCallLog(captured));

        var recovery = new McpPreflightRecovery(
            providers: [],
            enricher: enricher,
            logger: NullLogger<McpPreflightRecovery>.Instance);

        await recovery.TryRecoverAsync(
            "srv", "tool", missingFields: ["x"],
            existingArgs: new Dictionary<string, object?>(),
            parentSessionId: "session-42", CancellationToken.None);

        CollectionAssert.Contains(captured, "session-42",
            "enricher must be queried with the parent session id");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class StubProvider(string field, object value) : IToolArgumentDefaultsProvider
    {
        public bool CanResolve(string serverName, string toolName, string fieldName) =>
            string.Equals(fieldName, field, StringComparison.Ordinal);

        public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
            Task.FromResult<ResolvedDefault?>(new ResolvedDefault(value));
    }

    private sealed class ThrowingProvider(string field) : IToolArgumentDefaultsProvider
    {
        public bool CanResolve(string serverName, string toolName, string fieldName) =>
            string.Equals(fieldName, field, StringComparison.Ordinal);

        public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class EmptyToolCallLog : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
    }

    private sealed class CapturingToolCallLog(List<string?> captured) : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            captured.Add(sessionId);
            return Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
        }
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
    }
}
