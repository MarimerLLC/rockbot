using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

/// <summary>
/// Skill freshness for <c>mcp/{server}</c> skills (#615, ports mcp-aggregator#47). Fingerprints
/// are real <see cref="McpSurfaceFingerprint"/> values so these tests also pin the three ways the
/// aggregator's first version lied (aggregator#41): names-only hashing, an uncompared version,
/// and a failed read stored as the baseline.
/// </summary>
[TestClass]
public class McpSkillFreshnessTests
{
    private const string Schema = """{"type":"object","properties":{"start":{"type":"string"},"end":{"type":"string"}},"required":["start"]}""";
    private const string SchemaReordered = """{ "required": ["start"], "properties": { "end": {"type":"string"}, "start": {"type":"string"} }, "type": "object" }""";
    private const string SchemaChanged = """{"type":"object","properties":{"from":{"type":"string"},"end":{"type":"string"}},"required":["from"]}""";

    private static readonly DateTimeOffset Recorded = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Surface(string description = "Lists events.", string schema = Schema) =>
        McpSurfaceFingerprint.Server([("get_events", description, schema)], []);

    private static McpServerSummary Server(
        string name = "adjutant",
        string? fingerprint = null,
        string? version = "1.4.0",
        string? id = "id-adjutant",
        string? identity = "ident-adjutant") => new()
    {
        ServerName = name,
        ServerId = id,
        Fingerprint = fingerprint,
        Version = version,
        IdentityHash = identity
    };

    private static SkillSurfaceBaseline BaselineOf(McpServerSummary server) =>
        McpSkillFreshness.Capture(server.ServerName, server, Recorded).Baseline!;

    private static SkillFreshness Evaluate(SkillSurfaceBaseline? baseline, params McpServerSummary[] live) =>
        McpSkillFreshness.Evaluate(
            "adjutant",
            baseline,
            live.FirstOrDefault(s => s.ServerName == "adjutant"),
            live);

    // ── Fresh / stale ────────────────────────────────────────────────────────

    [TestMethod]
    public void SameSurface_IsFresh()
    {
        var server = Server(fingerprint: Surface());
        Assert.AreEqual(SkillFreshnessStatus.Fresh, Evaluate(BaselineOf(server), server).Status);
    }

    [TestMethod]
    public void DescriptionOnlyChange_IsStale()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        var result = Evaluate(baseline, Server(fingerprint: Surface(description: "Lists calendar events for one account.")));

        Assert.AreEqual(SkillFreshnessStatus.Stale, result.Status);
        StringAssert.Contains(result.Reason, "tool surface changed");
    }

    [TestMethod]
    public void SchemaOnlyChange_IsStale()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Stale, Evaluate(baseline, Server(fingerprint: Surface(schema: SchemaChanged))).Status);
    }

    [TestMethod]
    public void ReorderedSchemaKeys_StayFresh()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Fresh, Evaluate(baseline, Server(fingerprint: Surface(schema: SchemaReordered))).Status);
    }

    [TestMethod]
    public void VersionChange_IsStale_EvenWithTheSameSurface()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface(), version: "1.4.0"));
        var result = Evaluate(baseline, Server(fingerprint: Surface(), version: "2.0.0"));

        Assert.AreEqual(SkillFreshnessStatus.Stale, result.Status);
        StringAssert.Contains(result.Reason, "v1.4.0 to v2.0.0");
    }

    [TestMethod]
    public void VersionMissingOnEitherSide_FallsBackToTheFingerprint()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface(), version: null));
        Assert.AreEqual(SkillFreshnessStatus.Fresh, Evaluate(baseline, Server(fingerprint: Surface(), version: "2.0.0")).Status);
        Assert.AreEqual(SkillFreshnessStatus.Stale, Evaluate(baseline, Server(fingerprint: Surface(schema: SchemaChanged), version: "2.0.0")).Status);
    }

    [TestMethod]
    public void NameReusedByADifferentServer_IsStale()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        var result = Evaluate(baseline, Server(fingerprint: Surface(), id: "id-other", identity: "ident-other"));

        Assert.AreEqual(SkillFreshnessStatus.Stale, result.Status);
        StringAssert.Contains(result.Reason, "different MCP server");
    }

    [TestMethod]
    public void SameServerReRegistered_NewIdSameIdentity_ComparesTheSurface()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Fresh,
            Evaluate(baseline, Server(fingerprint: Surface(), id: "id-new")).Status);
    }

    // ── Unknown ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void NoBaseline_IsUnknown_NotStale()
    {
        Assert.AreEqual(SkillFreshnessStatus.Unknown, Evaluate(null, Server(fingerprint: Surface())).Status);
    }

    [TestMethod]
    public void CurrentSurfaceUnreadable_IsUnknown()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Unknown, Evaluate(baseline, Server(fingerprint: null)).Status);
    }

    [TestMethod]
    public void ServerNotConnected_IsUnknown()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Unknown, Evaluate(baseline).Status);
    }

    // ── Renamed ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ServerLiveUnderAnotherName_SameId_IsRenamed()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        var result = Evaluate(baseline, Server(name: "adjutant-v2", fingerprint: Surface(), identity: "ident-changed"));

        Assert.AreEqual(SkillFreshnessStatus.Renamed, result.Status);
        Assert.AreEqual("adjutant-v2", result.RenamedTo);
    }

    [TestMethod]
    public void ServerLiveUnderAnotherName_SameIdentity_IsRenamed()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        var result = Evaluate(baseline, Server(name: "calendar", fingerprint: Surface(), id: "id-new"));

        Assert.AreEqual(SkillFreshnessStatus.Renamed, result.Status);
        Assert.AreEqual("calendar", result.RenamedTo);
    }

    [TestMethod]
    public void UnrelatedServersOnly_IsUnknown_NotRenamed()
    {
        var baseline = BaselineOf(Server(fingerprint: Surface()));
        Assert.AreEqual(SkillFreshnessStatus.Unknown,
            Evaluate(baseline, Server(name: "ms365", fingerprint: Surface(), id: "id-ms365", identity: "ident-ms365")).Status);
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Capture_RecordsIdFingerprintVersionAndIdentity()
    {
        var server = Server(fingerprint: Surface());
        var capture = McpSkillFreshness.Capture("adjutant", server, Recorded);

        Assert.IsNull(capture.Reason);
        Assert.AreEqual(new SkillSurfaceBaseline("adjutant", "id-adjutant", Surface(), "1.4.0", "ident-adjutant", Recorded), capture.Baseline);
    }

    [TestMethod]
    public void Capture_FailedRead_RecordsNoBaseline_AndSaysWhy()
    {
        var unreadable = McpSkillFreshness.Capture("adjutant", Server(fingerprint: null), Recorded);
        Assert.IsNull(unreadable.Baseline);
        StringAssert.Contains(unreadable.Reason, "couldn't be read");

        var absent = McpSkillFreshness.Capture("adjutant", null, Recorded);
        Assert.IsNull(absent.Baseline);
        StringAssert.Contains(absent.Reason, "isn't connected");
    }

    // ── McpSkillNames ────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("mcp/adjutant", "adjutant")]
    [DataRow("mcp/Adjutant/calendar", "adjutant")]
    [DataRow("MCP/ms365", "ms365")]
    public void TryGetServerName_MatchesServerSkills(string skill, string server)
    {
        Assert.IsTrue(McpSkillNames.TryGetServerName(skill, out var actual));
        Assert.AreEqual(server, actual);
    }

    [TestMethod]
    [DataRow("mcp")]
    [DataRow("mcp/")]
    [DataRow("plan-meeting")]
    [DataRow("research/mcp/adjutant")]
    public void TryGetServerName_RejectsOtherSkills(string skill)
    {
        Assert.IsFalse(McpSkillNames.TryGetServerName(skill, out _));
    }

    // ── McpSkillSurface (index + schema cache) ───────────────────────────────

    [TestMethod]
    public void Surface_CaptureAndEvaluate_UseTheLiveIndex()
    {
        var index = new McpServerIndex();
        index.Apply(new McpServersIndexed { Servers = [Server(fingerprint: Surface())] });
        var surface = new McpSkillSurface(index, NoSchemas());

        var capture = surface.CaptureBaseline("mcp/adjutant/calendar");
        Assert.IsNotNull(capture.Baseline);

        var skill = new Skill("mcp/adjutant/calendar", "", "body", Recorded, SurfaceBaseline: capture.Baseline);
        Assert.AreEqual(SkillFreshnessStatus.Fresh, surface.Evaluate(skill).Status);

        index.Apply(new McpServersIndexed { Servers = [Server(fingerprint: Surface(schema: SchemaChanged))] });
        Assert.AreEqual(SkillFreshnessStatus.Stale, surface.Evaluate(skill).Status);
    }

    [TestMethod]
    public void Surface_CaptureForNonMcpSkill_RecordsNothing()
    {
        var surface = new McpSkillSurface(new McpServerIndex(), NoSchemas());
        Assert.IsNull(surface.CaptureBaseline("plan-meeting").Baseline);
    }

    [TestMethod]
    public async Task Surface_LiveSurfaceText_RendersToolsWithCanonicalSchemas()
    {
        var schemas = new ToolSchemaCache((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(
        [
            new McpToolDefinition { Name = "list_accounts", Description = "Lists accounts." },
            new McpToolDefinition { Name = "get_events", Description = "Lists events.", ParametersSchema = SchemaReordered }
        ]));
        var surface = new McpSkillSurface(new McpServerIndex(), schemas);

        var text = await surface.GetLiveSurfaceTextAsync("adjutant", CancellationToken.None);

        Assert.IsNotNull(text);
        Assert.IsTrue(text.IndexOf("### get_events", StringComparison.Ordinal) < text.IndexOf("### list_accounts", StringComparison.Ordinal),
            "tools are sorted by name");
        StringAssert.Contains(text, McpSurfaceFingerprint.Canonicalize(SchemaReordered));
        StringAssert.Contains(text, "Input schema: (none)");
    }

    [TestMethod]
    public async Task Surface_LiveSurfaceText_IsNullWhenSchemasUnavailable()
    {
        var surface = new McpSkillSurface(new McpServerIndex(), NoSchemas());
        Assert.IsNull(await surface.GetLiveSurfaceTextAsync("adjutant", CancellationToken.None));
    }

    private static ToolSchemaCache NoSchemas() =>
        new((_, _) => Task.FromResult<IReadOnlyList<McpToolDefinition>?>(null));
}
