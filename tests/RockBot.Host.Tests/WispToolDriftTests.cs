using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Memory;
using RockBot.Tools;

namespace RockBot.Host.Tests;

/// <summary>
/// Wisp freshness (#647): stored wisp resources record the fingerprint of each MCP tool they call,
/// every attach path records them, the dream drift pass sends a validated wisp back to provisional
/// when one of those tools changed, and re-promotion re-stamps them.
/// </summary>
[TestClass]
public class WispToolDriftTests
{
    private const string Body =
        """{"description":"Events","steps":[{"id":"e","mode":"Direct","gateway":"Mcp","server":"calendar-mcp","tool":"get_events","params":{}}]}""";

    private static readonly IReadOnlyDictionary<string, string> Recorded =
        new Dictionary<string, string> { ["calendar-mcp/get_events"] = "fp-old" };

    // ── Drift detection ──────────────────────────────────────────────────────

    [TestMethod]
    public void ChangedTools_MovedFingerprint_IsChanged()
    {
        var directory = new Directory { ["calendar-mcp/get_events"] = "fp-new" };
        CollectionAssert.AreEqual(new[] { "calendar-mcp/get_events" },
            DreamService.ChangedTools(Wisp(Recorded), directory).ToArray());
    }

    [TestMethod]
    public void ChangedTools_SameFingerprint_IsNot()
    {
        var directory = new Directory { ["calendar-mcp/get_events"] = "fp-old" };
        Assert.AreEqual(0, DreamService.ChangedTools(Wisp(Recorded), directory).Count);
    }

    [TestMethod]
    public void ChangedTools_ToolGoneFromAnIndexedServer_IsChanged()
    {
        var directory = new Directory { ["calendar-mcp/list_accounts"] = "fp-a" };
        Assert.AreEqual(1, DreamService.ChangedTools(Wisp(Recorded), directory).Count);
    }

    [TestMethod]
    public void ChangedTools_ServerNotIndexed_OrFingerprintUnknown_IsUnknownNotChanged()
    {
        Assert.AreEqual(0, DreamService.ChangedTools(Wisp(Recorded), new Directory()).Count);
        Assert.AreEqual(0, DreamService.ChangedTools(Wisp(Recorded), new Directory { ["calendar-mcp/get_events"] = null }).Count);
    }

    // ── The drift pass ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task DriftPass_FlagsChangedValidatedWisps_Only()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-20);
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow, Manifest:
        [
            Wisp(Recorded, "changed.json", created: created),
            Wisp(new Dictionary<string, string> { ["calendar-mcp/list_accounts"] = "fp-a" }, "unchanged.json", created: created),
            Wisp(Recorded, "already-provisional.json", provisional: true, created: created),
            Wisp(null, "no-fingerprints.json", created: created)
        ]));
        var directory = new Directory { ["calendar-mcp/get_events"] = "fp-new", ["calendar-mcp/list_accounts"] = "fp-a" };

        await CreateService(store, directory).RunWispToolDriftPassAsync(CancellationToken.None);

        var manifest = (await store.GetAsync("calendar/scan"))!.Manifest!.ToDictionary(r => r.Filename);
        var changed = manifest["changed.json"];
        Assert.IsTrue(changed.Provisional);
        StringAssert.StartsWith(changed.Description, "[tool changed: calendar-mcp/get_events] ");
        Assert.IsTrue(changed.CreatedAt > created, "the validation window restarts");
        Assert.AreEqual("fp-old", changed.ToolFingerprints!["calendar-mcp/get_events"],
            "the recorded fingerprints stay until the wisp is re-validated");

        Assert.IsFalse(manifest["unchanged.json"].Provisional);
        Assert.AreEqual(created, manifest["already-provisional.json"].CreatedAt, "provisional wisps are left to validation");
        Assert.IsFalse(manifest["no-fingerprints.json"].Provisional);
    }

    [TestMethod]
    public async Task DriftPass_Disabled_DoesNothing()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow, Manifest: [Wisp(Recorded)]));

        await CreateService(store, new Directory { ["calendar-mcp/get_events"] = "fp-new" },
                new DreamOptions { Enabled = false, WispToolDriftEnabled = false })
            .RunWispToolDriftPassAsync(CancellationToken.None);

        Assert.IsFalse((await store.GetAsync("calendar/scan"))!.Manifest!.Single().Provisional);
    }

    [TestMethod]
    public async Task Repromotion_RestampsFingerprints_AndStripsTheTag()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow));
        var flagged = Wisp(Recorded, provisional: true) with { Description = "[tool changed: calendar-mcp/get_events] Events" };
        await store.AttachResourceAsync("calendar/scan",
            new SkillResourceInput(flagged.Filename, SkillResourceType.Wisp, flagged.Description, Body), flagged);

        var restamped = await CreateService(store, new Directory { ["calendar-mcp/get_events"] = "fp-new" })
            .RestampPromotedWispAsync("calendar/scan", flagged);

        Assert.AreEqual("Events", restamped.Description);
        Assert.AreEqual("fp-new", restamped.ToolFingerprints!["calendar-mcp/get_events"]);
    }

    // ── Attach paths record fingerprints ─────────────────────────────────────

    [TestMethod]
    public async Task WispSuccessPromotion_RecordsToolFingerprints()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow));
        var candidate = new DreamService.WispSuccessCandidate("hash1", 5, 3, "Events", "calendar/scan", Body);

        await DreamService.ApplyWispSuccessPromotionsAsync(
            store, [candidate], new HashSet<string> { "calendar/scan" },
            [new DreamService.WispSuccessPromotionDto { TargetSkill = "calendar/scan", Filename = "events.json", DefinitionHash = "hash1" }],
            NullLogger.Instance, CancellationToken.None,
            new Directory { ["calendar-mcp/get_events"] = "fp-now" });

        Assert.AreEqual("fp-now", (await store.GetAsync("calendar/scan"))!.Manifest!.Single().ToolFingerprints!["calendar-mcp/get_events"]);
    }

    [TestMethod]
    public async Task RepairTicketAttach_RecordsToolFingerprints()
    {
        var store = new InMemorySkillResourceStore();
        await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow));
        var applier = new SkillResourceApplier(store, NullLogger<SkillResourceApplier>.Instance,
            new Directory { ["calendar-mcp/get_events"] = "fp-now" });

        await applier.ApplyAsync(Ticket(new { skill = "calendar/scan", filename = "events.json", op = "attach", type = "Wisp", content = Body }),
            CancellationToken.None);

        Assert.AreEqual("fp-now", (await store.GetAsync("calendar/scan"))!.Manifest!.Single().ToolFingerprints!["calendar-mcp/get_events"]);
    }

    [TestMethod]
    public async Task FileSkillStore_CarriesFingerprints_ThroughBulkSaveAndDerivedAttach()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rockbot-wisp-fp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSkillStore(
                Options.Create(new SkillOptions { BasePath = dir }),
                Options.Create(new AgentProfileOptions()),
                Options.Create(new EmbeddingOptions()),
                NullLogger<FileSkillStore>.Instance,
                EmbeddingTextPreparer.ForTests());

            await store.SaveAsync(new Skill("calendar/scan", "s", "# scan", DateTimeOffset.UtcNow),
                [new SkillResourceInput("bulk.json", SkillResourceType.Wisp, "d", Body, ToolFingerprints: Recorded)]);
            await store.AttachResourceAsync("calendar/scan",
                new SkillResourceInput("attached.json", SkillResourceType.Wisp, "d", Body, ToolFingerprints: Recorded));

            // A fresh store reads what the first one wrote.
            var reread = new FileSkillStore(
                Options.Create(new SkillOptions { BasePath = dir }),
                Options.Create(new AgentProfileOptions()),
                Options.Create(new EmbeddingOptions()),
                NullLogger<FileSkillStore>.Instance,
                EmbeddingTextPreparer.ForTests());
            var manifest = (await reread.GetAsync("calendar/scan"))!.Manifest!.ToDictionary(r => r.Filename);
            Assert.AreEqual("fp-old", manifest["bulk.json"].ToolFingerprints!["calendar-mcp/get_events"]);
            Assert.AreEqual("fp-old", manifest["attached.json"].ToolFingerprints!["calendar-mcp/get_events"]);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void SaveSkillSchema_DoesNotExposeToolFingerprints()
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(SkillResourceInput)).GetRawText();
        Assert.IsFalse(schema.Contains("oolFingerprints", StringComparison.Ordinal),
            "fingerprints are set by the code that attaches a wisp, never by the model");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SkillResource Wisp(
        IReadOnlyDictionary<string, string>? fingerprints,
        string filename = "events.json",
        bool provisional = false,
        DateTimeOffset? created = null) =>
        new(filename, SkillResourceType.Wisp, "Events", provisional, created ?? DateTimeOffset.UtcNow.AddDays(-5),
            ToolFingerprints: fingerprints);

    private static RepairTicket Ticket(object change) =>
        new(Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.SkillResource,
            Change: JsonSerializer.SerializeToElement(change),
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static DreamService CreateService(ISkillStore store, IMcpToolDirectory directory, DreamOptions? options = null) =>
        new(new EmptyLongTermMemory(),
            [store],
            new NoLlm(),
            new AgentWorkSerializer(),
            new IdleActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(options ?? new DreamOptions { Enabled = false }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance,
            mcpToolDirectory: directory);

    /// <summary>
    /// A directory over <c>server/tool → fingerprint</c> entries. A server is indexed when any of
    /// its tools is listed; a null value is a tool whose fingerprint is unknown.
    /// </summary>
    private sealed class Directory : Dictionary<string, string?>, IMcpToolDirectory
    {
        public IToolExecutor WrapperExecutor => throw new NotSupportedException();

        public McpToolEntry? Resolve(string? serverName, string tool) =>
            serverName is not null && TryGetValue(WispToolFingerprints.Key(serverName, tool), out var fp)
                ? new McpToolEntry(serverName, tool, null, fp)
                : null;

        public IReadOnlyList<McpToolEntry> ForServer(string serverName) => [];

        public bool IsServerIndexed(string serverName) =>
            Keys.Any(k => k.StartsWith(serverName + "/", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NoLlm : ILlmClient
    {
        public bool IsIdle => true;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the drift pass makes no LLM call");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class IdleActivityMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
    }

    private sealed class EmptyLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
