using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

/// <summary>
/// Coverage for the two MCP skill-injection paths:
/// pre-flight on <c>mcp_get_service_details</c> and recovery-time inside
/// <see cref="SchemaErrorEnricher"/>. The shared helper
/// <see cref="McpServerSkillFormatter"/> is exercised through the public surface
/// of both call sites so the assertions reflect what the LLM actually sees.
/// </summary>
[TestClass]
public class McpSkillInjectionTests
{
    private static readonly AgentIdentity Identity = new("test-agent");

    // ── Pre-flight: mcp_get_service_details ──────────────────────────────────

    [TestMethod]
    public async Task GetServiceDetails_AppendsMatchingSkill()
    {
        var skills = new SkillSeed()
            .With("mcp/filesystem", "FS server skill", "# Use FS\nCall read_file with `path` (required).")
            .Build();
        var (executor, publisher, subscriber) = NewExecutor(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        StringAssert.Contains(result.Content!, "[mcp-skill-injection]");
        StringAssert.Contains(result.Content!, "mcp/filesystem");
        StringAssert.Contains(result.Content!, "Call read_file with `path` (required).");
    }

    [TestMethod]
    public async Task GetServiceDetails_AppendsMatchingSubSkills()
    {
        var skills = new SkillSeed()
            .With("mcp/calendar-mcp", "Calendar server", "# Calendar")
            .With("mcp/calendar-mcp/calendar-operations", "Calendar ops", "## get_calendar_events\nRequires accountId.")
            .With("mcp/unrelated", "Other", "should not appear")
            .Build();
        var (executor, publisher, subscriber) = NewExecutor(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "calendar-mcp");

        StringAssert.Contains(result.Content!, "mcp/calendar-mcp");
        StringAssert.Contains(result.Content!, "mcp/calendar-mcp/calendar-operations");
        StringAssert.Contains(result.Content!, "Requires accountId.");
        Assert.IsFalse(result.Content!.Contains("mcp/unrelated"),
            "Skills for other servers must not be injected");
    }

    [TestMethod]
    public async Task GetServiceDetails_NoMatchingSkill_LeavesResponseIntact()
    {
        var skills = new SkillSeed()
            .With("mcp/something-else", "x", "x")
            .Build();
        var (executor, publisher, subscriber) = NewExecutor(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        Assert.IsFalse(result.Content!.Contains("[mcp-skill-injection]"));
        // Should still parse cleanly as the original JSON payload (the appended
        // block only lands when there is a skill to append).
        var doc = JsonDocument.Parse(result.Content!);
        Assert.AreEqual("filesystem", doc.RootElement.GetProperty("server").GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task GetServiceDetails_NoSkillStore_LeavesResponseIntact()
    {
        var (executor, publisher, subscriber) = NewExecutor(skillStore: null);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        Assert.IsFalse(result.Content!.Contains("[mcp-skill-injection]"));
    }

    [TestMethod]
    public async Task ListServices_DoesNotTriggerInjection()
    {
        // The whole point of skipping list_services is that it returns many
        // servers; injecting every matching skill would crowd the context.
        var skills = new SkillSeed()
            .With("mcp/filesystem", "FS", "# FS body")
            .Build();
        var (executor, _, _) = NewExecutor(skills);

        var result = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "list-1",
            ToolName = "mcp_list_services"
        }, CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsFalse(result.Content!.Contains("[mcp-skill-injection]"),
            "mcp_list_services must not append skill blocks");
        Assert.IsFalse(result.Content!.Contains("# FS body"));
    }

    [TestMethod]
    public async Task GetServiceDetails_LargeSkillIsTruncated()
    {
        var bigBody = new string('x', McpServerSkillFormatter.PerSkillContentCap + 500);
        var skills = new SkillSeed()
            .With("mcp/filesystem", "big", bigBody)
            .Build();
        var (executor, publisher, subscriber) = NewExecutor(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        StringAssert.Contains(result.Content!, "[mcp-skill-injection]");
        StringAssert.Contains(result.Content!, "truncated");
        StringAssert.Contains(result.Content!, "get_skill(\"mcp/filesystem\")");
    }

    // ── Recovery-time: SchemaErrorEnricher ───────────────────────────────────

    [TestMethod]
    public async Task SchemaErrorEnricher_AppendsServerSkill()
    {
        var skills = new SkillSeed()
            .With("mcp/calendar-mcp", "Calendar", "## get_calendar_events\nRequires accountId.")
            .Build();
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new EmptyToolCallLog(),
            skills);

        var result = await enricher.EnrichAsync(
            serverName: "calendar-mcp",
            toolName: "get_calendar_events",
            fieldName: "accountId",
            sessionId: null,
            originalError: "Required parameter 'accountId' was not provided",
            CancellationToken.None);

        StringAssert.StartsWith(result, "Required parameter 'accountId'");
        StringAssert.Contains(result, "[mcp-recovery]");
        StringAssert.Contains(result, "[mcp-skill-injection]");
        StringAssert.Contains(result, "Requires accountId.");
    }

    [TestMethod]
    public async Task SchemaErrorEnricher_NoMatchingSkill_StillEnrichesNormally()
    {
        var skills = new SkillSeed()
            .With("mcp/something-else", "x", "x")
            .Build();
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new EmptyToolCallLog(),
            skills);

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_calendar_events", "accountId",
            sessionId: null,
            originalError: "Required parameter 'accountId' was not provided",
            CancellationToken.None);

        StringAssert.Contains(result, "[mcp-recovery]");
        Assert.IsFalse(result.Contains("[mcp-skill-injection]"),
            "Recovery output should be unchanged when no matching skill exists");
    }

    [TestMethod]
    public async Task SchemaErrorEnricher_NoSkillStore_PreservesExistingBehavior()
    {
        // Verifies the new ctor parameter is genuinely optional — null skillStore
        // means recovery output looks exactly like it did before this change.
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new EmptyToolCallLog());

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_calendar_events", "accountId",
            sessionId: null,
            originalError: "Required parameter 'accountId' was not provided",
            CancellationToken.None);

        StringAssert.Contains(result, "[mcp-recovery]");
        Assert.IsFalse(result.Contains("[mcp-skill-injection]"));
    }

    // ── Freshness marker (#615) ──────────────────────────────────────────────

    private const string CurrentFingerprint = "fp-current";

    private static SkillSurfaceBaseline BaselineFor(string server, string fingerprint) =>
        new(server, $"id-{server}", fingerprint, null, $"ident-{server}", DateTimeOffset.UtcNow.AddDays(-3));

    [TestMethod]
    public async Task GetServiceDetails_StaleSkill_CarriesTheStaleMarker()
    {
        var skills = new SkillSeed()
            .With("mcp/filesystem", "FS", "Call read_file with `file`.", BaselineFor("filesystem", "fp-old"))
            .Build();
        var (executor, publisher, subscriber) = NewExecutorWithSurface(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        StringAssert.Contains(result.Content!,
            "[stale: filesystem's tool surface changed since this skill was written; trust the schema above where they disagree]");
        Assert.IsTrue(
            result.Content!.IndexOf("[stale:", StringComparison.Ordinal) < result.Content!.IndexOf("Call read_file", StringComparison.Ordinal),
            "the marker precedes the skill body");
    }

    [TestMethod]
    public async Task GetServiceDetails_FreshOrUnknownSkill_HasNoMarker()
    {
        var skills = new SkillSeed()
            .With("mcp/filesystem", "FS", "fresh body", BaselineFor("filesystem", CurrentFingerprint))
            .With("mcp/filesystem/legacy", "FS legacy", "no-baseline body")
            .Build();
        var (executor, publisher, subscriber) = NewExecutorWithSurface(skills);

        var result = await RunGetServiceDetailsAsync(executor, publisher, subscriber, "filesystem");

        StringAssert.Contains(result.Content!, "fresh body");
        StringAssert.Contains(result.Content!, "no-baseline body");
        Assert.IsFalse(result.Content!.Contains("[stale:"), "fresh and unknown skills are not marked");
    }

    [TestMethod]
    public async Task SchemaErrorEnricher_StaleSkill_CarriesTheStaleMarker()
    {
        var skills = new SkillSeed()
            .With("mcp/calendar-mcp", "Calendar", "Requires accountId.", BaselineFor("calendar-mcp", "fp-old"))
            .Build();
        var enricher = new SchemaErrorEnricher(
            new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null)),
            new EmptyToolCallLog(),
            skills,
            new McpSkillSurface(IndexWithFingerprints(), NoSchemas()));

        var result = await enricher.EnrichAsync(
            "calendar-mcp", "get_calendar_events", "accountId",
            sessionId: null,
            originalError: "Required parameter 'accountId' was not provided",
            CancellationToken.None);

        StringAssert.Contains(result, "[mcp-skill-injection]");
        StringAssert.Contains(result, "[stale: calendar-mcp's tool surface changed since this skill was written");
    }

    [TestMethod]
    public async Task ListServices_ReportsSkillsWrittenForARenamedServer()
    {
        // mcp/old-fs was written for the server that now runs as "filesystem" (same id).
        var skills = new SkillSeed()
            .With("mcp/old-fs", "FS", "body", BaselineFor("old-fs", CurrentFingerprint) with { ServerId = "id-filesystem" })
            .With("mcp/calendar-mcp", "Calendar", "body", BaselineFor("calendar-mcp", CurrentFingerprint))
            .Build();
        var (executor, _, _) = NewExecutorWithSurface(skills);

        var result = await executor.ExecuteAsync(
            new ToolInvokeRequest { ToolCallId = "c", ToolName = "mcp_list_services", Arguments = "{}" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content!);
        var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("serverName").GetString()!);

        var renamed = rows["filesystem"].GetProperty("skillsWrittenUnderPreviousName");
        Assert.AreEqual(1, renamed.GetArrayLength());
        Assert.AreEqual("mcp/old-fs", renamed[0].GetProperty("skill").GetString());
        Assert.AreEqual("old-fs", renamed[0].GetProperty("previousServerName").GetString());
        Assert.IsFalse(rows["calendar-mcp"].TryGetProperty("skillsWrittenUnderPreviousName", out _),
            "the field is omitted when there is nothing to report");
    }

    private static McpServerIndex IndexWithFingerprints()
    {
        var index = new McpServerIndex();
        index.Apply(new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem", ServerId = "id-filesystem", IdentityHash = "ident-filesystem",
                    Fingerprint = CurrentFingerprint, Summary = "FS tools", ToolCount = 1, ToolNames = ["read_file"]
                },
                new McpServerSummary
                {
                    ServerName = "calendar-mcp", ServerId = "id-calendar-mcp", IdentityHash = "ident-calendar-mcp",
                    Fingerprint = CurrentFingerprint, Summary = "Calendar tools", ToolCount = 1, ToolNames = ["get_calendar_events"]
                }
            ]
        });
        return index;
    }

    private static ToolSchemaCache NoSchemas() =>
        new((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null));

    private static (McpManagementExecutor Executor, TrackingPublisher Publisher, StubSubscriber Subscriber)
        NewExecutorWithSurface(InMemorySkillStore skillStore)
    {
        var publisher = new TrackingPublisher();
        var subscriber = new StubSubscriber();
        var proxy = new McpToolProxy(publisher, subscriber, Identity, NullLogger<McpToolProxy>.Instance);
        var index = IndexWithFingerprints();

        var executor = new McpManagementExecutor(
            index, proxy, publisher, subscriber, Identity,
            NullLogger<McpManagementExecutor>.Instance,
            timeout: null, recovery: null, skillStore: skillStore,
            skillSurface: new McpSkillSurface(index, NoSchemas()));
        return (executor, publisher, subscriber);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (McpManagementExecutor Executor, TrackingPublisher Publisher, StubSubscriber Subscriber)
        NewExecutor(InMemorySkillStore? skillStore = null)
    {
        var publisher = new TrackingPublisher();
        var subscriber = new StubSubscriber();
        var proxy = new McpToolProxy(publisher, subscriber, Identity, NullLogger<McpToolProxy>.Instance);

        var index = new McpServerIndex();
        index.Apply(new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem",
                    Summary = "FS tools",
                    ToolCount = 1,
                    ToolNames = ["read_file"]
                },
                new McpServerSummary
                {
                    ServerName = "calendar-mcp",
                    Summary = "Calendar tools",
                    ToolCount = 1,
                    ToolNames = ["get_calendar_events"]
                }
            ]
        });

        var executor = new McpManagementExecutor(
            index, proxy, publisher, subscriber, Identity,
            NullLogger<McpManagementExecutor>.Instance,
            timeout: null, recovery: null, skillStore: skillStore);
        return (executor, publisher, subscriber);
    }

    private static async Task<ToolInvokeResponse> RunGetServiceDetailsAsync(
        McpManagementExecutor executor, TrackingPublisher publisher, StubSubscriber subscriber,
        string serverName)
    {
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = $$$"""{"server_name":"{{{serverName}}}"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(50);

        Assert.AreEqual(1, publisher.Published.Count, "expected one management request");
        var published = publisher.Published[0].Envelope;
        var response = new McpGetServiceDetailsResponse
        {
            ServerName = serverName,
            Tools = [new McpToolDefinition { Name = "stub_tool", Description = "stub" }]
        };
        var envelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, envelope);

        return await executeTask;
    }
}

internal sealed class SkillSeed
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);

    public SkillSeed With(string name, string summary, string content, SkillSurfaceBaseline? baseline = null)
    {
        _skills[name] = new Skill(name, summary, content, DateTimeOffset.UtcNow, SurfaceBaseline: baseline);
        return this;
    }

    public InMemorySkillStore Build() => new(_skills);
}

internal sealed class InMemorySkillStore(Dictionary<string, Skill>? seed = null) : ISkillStore
{
    private readonly Dictionary<string, Skill> _skills = seed ?? new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(Skill skill)
    {
        _skills[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public Task<Skill?> GetAsync(string name) => Task.FromResult(_skills.GetValueOrDefault(name));

    public Task<IReadOnlyList<Skill>> ListAsync() =>
        Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());

    public Task DeleteAsync(string name)
    {
        _skills.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default,
        float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}

internal sealed class EmptyToolCallLog : IToolCallLog
{
    public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
    public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolCallEvent>>([]);
}
