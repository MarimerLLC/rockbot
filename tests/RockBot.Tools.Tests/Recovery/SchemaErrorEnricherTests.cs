using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class SchemaErrorEnricherTests
{
    private static SchemaErrorEnricher NewEnricher(
        IReadOnlyDictionary<string, IReadOnlyList<McpToolDefinition>>? schemasByServer = null,
        IReadOnlyList<ToolCallEvent>? sessionCalls = null,
        string? expectedSessionId = null)
    {
        var cache = new ToolSchemaCache((server, _) =>
        {
            if (schemasByServer is not null && schemasByServer.TryGetValue(server, out var list))
                return Task.FromResult<IReadOnlyList<McpToolDefinition>?>(list);
            return Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null);
        });

        var log = new StubToolCallLog(sessionCalls ?? [], expectedSessionId);
        return new SchemaErrorEnricher(cache, log);
    }

    [TestMethod]
    public async Task NoSchema_NoSession_StartsWithOriginalError_AndStatesMissingField()
    {
        var enricher = NewEnricher();

        var result = await enricher.EnrichAsync(
            serverName: "srv", toolName: "do", fieldName: "x",
            sessionId: null,
            originalError: "Required parameter 'x' was not provided",
            default);

        StringAssert.StartsWith(result, "Required parameter 'x' was not provided");
        StringAssert.Contains(result, "[mcp-recovery]");
        StringAssert.Contains(result, "missing required field 'x'");
    }

    [TestMethod]
    public async Task WithSchema_IncludesFieldSchemaAndHint()
    {
        var schemasByServer = new Dictionary<string, IReadOnlyList<McpToolDefinition>>
        {
            ["calendar-mcp"] = new[]
            {
                new McpToolDefinition
                {
                    Name = "get_email_details",
                    Description = "Get full email content. Both accountId and emailId are required — obtain them from search_emails results.",
                    ParametersSchema = """{"type":"object","properties":{"accountId":{"type":"string","description":"Account id"},"emailId":{"type":"string","description":"Email id from a search result"}},"required":["accountId","emailId"]}"""
                }
            }
        };
        var enricher = NewEnricher(schemasByServer);

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_email_details", "emailId",
            sessionId: null,
            originalError: "Required parameter 'emailId' was not provided",
            default);

        StringAssert.Contains(result, "Field schema:");
        StringAssert.Contains(result, "Email id from a search result");
        StringAssert.Contains(result, "Tool description hint:");
        StringAssert.Contains(result, "search_emails");
    }

    [TestMethod]
    public async Task WithSessionLog_ListsRecentRelatedCallsByInnerToolName()
    {
        // The chat client logs all MCP calls as ToolName="mcp_invoke_tool" with the
        // inner tool name surfacing via ArgumentsSummary. The enricher must search
        // both fields so calls to search_emails are matched for an emailId fault.
        var now = DateTimeOffset.UtcNow;
        var sessionCalls = new[]
        {
            new ToolCallEvent("sid", "mcp_invoke_tool", "server_name=calendar-mcp, tool_name=search_emails, arguments={}",
                Succeeded: true, DurationMs: 10, Timestamp: now.AddMinutes(-2)),
            new ToolCallEvent("sid", "save_to_working_memory", "key=foo",
                Succeeded: true, DurationMs: 5, Timestamp: now.AddMinutes(-1)),
        };
        var enricher = NewEnricher(sessionCalls: sessionCalls, expectedSessionId: "sid");

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_email_details", "emailId",
            sessionId: "sid",
            originalError: "Required parameter 'emailId' was not provided",
            default);

        StringAssert.Contains(result, "Recent successful calls in this session");
        StringAssert.Contains(result, "search_emails");
        Assert.IsFalse(result.Contains("save_to_working_memory"),
            "unrelated session calls must not be listed");
    }

    [TestMethod]
    public async Task IgnoresFailedSessionCalls()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionCalls = new[]
        {
            // A failed search_emails — should not be listed as a likely source.
            new ToolCallEvent("sid", "mcp_invoke_tool", "tool_name=search_emails",
                Succeeded: false, DurationMs: 10, Timestamp: now.AddMinutes(-1))
        };
        var enricher = NewEnricher(sessionCalls: sessionCalls, expectedSessionId: "sid");

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_email_details", "emailId",
            sessionId: "sid",
            originalError: "Required parameter 'emailId' was not provided",
            default);

        Assert.IsFalse(result.Contains("Recent successful calls"));
    }

    [TestMethod]
    public async Task IgnoresStaleSessionCalls()
    {
        var sessionCalls = new[]
        {
            new ToolCallEvent("sid", "mcp_invoke_tool", "tool_name=search_emails",
                Succeeded: true, DurationMs: 10,
                Timestamp: DateTimeOffset.UtcNow.AddHours(-3))
        };
        var enricher = NewEnricher(sessionCalls: sessionCalls, expectedSessionId: "sid");

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_email_details", "emailId",
            sessionId: "sid",
            originalError: "Required parameter 'emailId' was not provided",
            default);

        Assert.IsFalse(result.Contains("Recent successful calls"),
            "calls older than the lookback window must not be listed");
    }

    [TestMethod]
    public async Task SchemaFetchThrows_StillEnriches()
    {
        var cache = new ToolSchemaCache((_, _) => throw new InvalidOperationException("bridge offline"));
        var enricher = new SchemaErrorEnricher(cache, new StubToolCallLog([], null));

        var result = await enricher.EnrichAsync(
            "srv", "do", "x", sessionId: null,
            originalError: "Required parameter 'x'", default);

        StringAssert.StartsWith(result, "Required parameter 'x'");
        StringAssert.Contains(result, "[mcp-recovery]");
    }

    [TestMethod]
    public async Task SessionLogThrows_StillEnriches()
    {
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new ThrowingToolCallLog());

        var result = await enricher.EnrichAsync(
            "srv", "do", "x", sessionId: "sid",
            originalError: "Required parameter 'x'", default);

        StringAssert.StartsWith(result, "Required parameter 'x'");
        Assert.IsFalse(result.Contains("Recent successful calls"));
    }

    [TestMethod]
    public void ExtractFieldSchema_ReturnsRawJsonForExistingField()
    {
        var schema = """{"type":"object","properties":{"emailId":{"type":"string","description":"Email id"}}}""";
        var extracted = SchemaErrorEnricher.ExtractFieldSchema(schema, "emailId");
        Assert.IsNotNull(extracted);
        StringAssert.Contains(extracted!, "Email id");
    }

    [TestMethod]
    public void ExtractFieldSchema_ReturnsNullForMissingField()
    {
        var schema = """{"type":"object","properties":{"foo":{"type":"string"}}}""";
        Assert.IsNull(SchemaErrorEnricher.ExtractFieldSchema(schema, "bar"));
    }

    [TestMethod]
    public void ExtractFieldSchema_ToleratesMalformedJson()
    {
        Assert.IsNull(SchemaErrorEnricher.ExtractFieldSchema("not json{", "x"));
        Assert.IsNull(SchemaErrorEnricher.ExtractFieldSchema(null, "x"));
        Assert.IsNull(SchemaErrorEnricher.ExtractFieldSchema("", "x"));
    }

    [TestMethod]
    public void ExtractFieldHint_ReturnsFirstMatchingSentence()
    {
        var desc = "Get an email. Both accountId and emailId are required. Cached for 60s.";
        var hint = SchemaErrorEnricher.ExtractFieldHint(desc, "emailId");
        Assert.IsNotNull(hint);
        StringAssert.Contains(hint!, "accountId");
        StringAssert.Contains(hint!, "emailId");
    }

    [TestMethod]
    public void ExtractFieldHint_NullWhenFieldNotMentioned()
    {
        var desc = "Just a generic tool description with no field references.";
        Assert.IsNull(SchemaErrorEnricher.ExtractFieldHint(desc, "emailId"));
    }

    [TestMethod]
    public void FieldRoot_StripsIdSuffix()
    {
        Assert.AreEqual("email", SchemaErrorEnricher.FieldRoot("emailId"));
        Assert.AreEqual("account", SchemaErrorEnricher.FieldRoot("accountId"));
        Assert.AreEqual("account", SchemaErrorEnricher.FieldRoot("account_id"));
        Assert.AreEqual("ACCOUNT", SchemaErrorEnricher.FieldRoot("ACCOUNTId"));
        // No suffix — returns unchanged.
        Assert.AreEqual("timeZone", SchemaErrorEnricher.FieldRoot("timeZone"));
        // Edge: pure "Id" stays as-is.
        Assert.AreEqual("Id", SchemaErrorEnricher.FieldRoot("Id"));
    }

    [TestMethod]
    public void CouldProduce_MatchesInnerToolNameViaArgsSummary()
    {
        var ev = new ToolCallEvent("sid", "mcp_invoke_tool",
            "server_name=calendar-mcp, tool_name=search_emails",
            Succeeded: true, DurationMs: 0, Timestamp: DateTimeOffset.UtcNow);

        Assert.IsTrue(SchemaErrorEnricher.CouldProduce(ev, "email"),
            "field root 'email' matches inner tool name 'search_emails' via ArgumentsSummary");

        Assert.IsFalse(SchemaErrorEnricher.CouldProduce(ev, "directory"),
            "unrelated field root must not match");
    }

    [TestMethod]
    public void CouldProduce_IgnoresVeryShortFieldRoot()
    {
        var ev = new ToolCallEvent("sid", "any", "x",
            Succeeded: true, DurationMs: 0, Timestamp: DateTimeOffset.UtcNow);
        Assert.IsFalse(SchemaErrorEnricher.CouldProduce(ev, "x"),
            "single-character roots would match everything — must be skipped");
    }

    private sealed class StubToolCallLog(
        IReadOnlyList<ToolCallEvent> events, string? expectedSessionId) : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            if (expectedSessionId is not null && sessionId != expectedSessionId)
                return Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
            return Task.FromResult(events);
        }

        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingToolCallLog : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            throw new InvalidOperationException("log offline");
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
