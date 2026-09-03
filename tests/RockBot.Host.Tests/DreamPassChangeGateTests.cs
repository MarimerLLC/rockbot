using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the change gate that stops corpus-wide dream passes from re-sending an unchanged
/// corpus to the LLM on every cycle.
/// </summary>
[TestClass]
public class DreamPassChangeGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    // ── ShouldSkip decision ──────────────────────────────────────────────────

    [TestMethod]
    public void ShouldSkip_NeverRun_RunsThePass()
    {
        Assert.IsFalse(
            DreamPassLedger.ShouldSkip(record: null, "abc", Now, Week),
            "A pass with no ledger entry has never run and must not be skipped.");
    }

    [TestMethod]
    public void ShouldSkip_UnchangedFingerprintWithinFloor_SkipsThePass()
    {
        var record = new DreamPassLedger.PassRecord("abc", Now.AddDays(-1));

        Assert.IsTrue(DreamPassLedger.ShouldSkip(record, "abc", Now, Week));
    }

    [TestMethod]
    public void ShouldSkip_ChangedFingerprint_RunsThePass()
    {
        var record = new DreamPassLedger.PassRecord("abc", Now.AddMinutes(-1));

        Assert.IsFalse(
            DreamPassLedger.ShouldSkip(record, "def", Now, Week),
            "A moved corpus must re-open the pass immediately, not wait for the floor.");
    }

    [TestMethod]
    public void ShouldSkip_UnchangedButFloorElapsed_RunsThePass()
    {
        var record = new DreamPassLedger.PassRecord("abc", Now.AddDays(-7).AddSeconds(-1));

        Assert.IsFalse(
            DreamPassLedger.ShouldSkip(record, "abc", Now, Week),
            "The max-skip floor exists so time-dependent directives (graph staleness pruning) still fire.");
    }

    [TestMethod]
    public void ShouldSkip_FloorDisabled_SkipsIndefinitely()
    {
        var record = new DreamPassLedger.PassRecord("abc", Now.AddYears(-1));

        Assert.IsTrue(
            DreamPassLedger.ShouldSkip(record, "abc", Now, TimeSpan.Zero),
            "A non-positive interval makes the gate absolute.");
    }

    [TestMethod]
    public void ShouldSkip_ClockWentBackwards_DoesNotForceARun()
    {
        // A restore or timezone change can put LastRunAt in the future. That must read as
        // "not yet due" rather than as a negative age that trips the floor.
        var record = new DreamPassLedger.PassRecord("abc", Now.AddDays(3));

        Assert.IsTrue(DreamPassLedger.ShouldSkip(record, "abc", Now, Week));
    }

    // ── Round-tripping ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Ledger_RoundTripsThroughDisk()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, DreamPassLedger.FileName);

            var ledger = await DreamPassLedger.LoadAsync(path, NullLogger.Instance);
            ledger.Record("graph consolidation", "fingerprint-1", Now);
            await ledger.SaveAsync();

            var reloaded = await DreamPassLedger.LoadAsync(path, NullLogger.Instance);

            Assert.IsTrue(
                reloaded.ShouldSkip("graph consolidation", "fingerprint-1", Now.AddHours(12), Week),
                "The gate must survive a pod restart, or every restart re-runs every gated pass.");
            Assert.IsFalse(reloaded.ShouldSkip("graph consolidation", "fingerprint-2", Now.AddHours(12), Week));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public async Task Ledger_CorruptFile_DegradesToRunningEverything()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, DreamPassLedger.FileName);
            await File.WriteAllTextAsync(path, "{ this is not json");

            var ledger = await DreamPassLedger.LoadAsync(path, NullLogger.Instance);

            Assert.IsFalse(
                ledger.ShouldSkip("graph consolidation", "anything", Now, Week),
                "A damaged ledger must cost an extra pass, never silently suppress one forever.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public async Task Ledger_NotDirty_WritesNothing()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, DreamPassLedger.FileName);
            var ledger = await DreamPassLedger.LoadAsync(path, NullLogger.Instance);

            await ledger.SaveAsync();

            Assert.IsFalse(File.Exists(path), "A cycle that gated nothing should not touch the PVC.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Fingerprint stability ────────────────────────────────────────────────

    [TestMethod]
    public void SkillCorpusFingerprint_IsOrderIndependent()
    {
        var a = MakeSkill("alpha", "does alpha");
        var b = MakeSkill("beta", "does beta");

        Assert.AreEqual(
            DreamService.SkillCorpusFingerprint([a, b], []),
            DreamService.SkillCorpusFingerprint([b, a], []),
            "Store enumeration order is not guaranteed; an order-sensitive hash reports phantom changes.");
    }

    [TestMethod]
    public void SkillCorpusFingerprint_ContentEdit_ChangesHash()
    {
        var before = MakeSkill("alpha", "does alpha");
        var after = before with { Content = "does alpha, but better" };

        Assert.AreNotEqual(
            DreamService.SkillCorpusFingerprint([before], []),
            DreamService.SkillCorpusFingerprint([after], []));
    }

    [TestMethod]
    public void SkillCorpusFingerprint_IgnoresUsageTimestamps()
    {
        // LastUsedAt moves on every get_skill call and UpdatedAt on every rewrite, but neither
        // reaches the consolidation prompt. Hashing them would re-open a whole-catalog merge for
        // a skill whose text is identical.
        var before = MakeSkill("alpha", "does alpha");
        var after = before with
        {
            LastUsedAt = Now,
            UpdatedAt = Now
        };

        Assert.AreEqual(
            DreamService.SkillCorpusFingerprint([before], []),
            DreamService.SkillCorpusFingerprint([after], []));
    }

    [TestMethod]
    public void SkillCorpusFingerprint_SingletonPrefixChange_ChangesHash()
    {
        var skill = MakeSkill("mcp/todo", "todo server");

        Assert.AreNotEqual(
            DreamService.SkillCorpusFingerprint([skill], []),
            DreamService.SkillCorpusFingerprint([skill], ["mcp/"]),
            "Prefixes change the constraints paragraph the model is given, so they are part of the input.");
    }

    [TestMethod]
    public void GraphFingerprint_IsOrderIndependent()
    {
        var e1 = MakeEntity("e1", "Rocky");
        var e2 = MakeEntity("e2", "RockBot");
        var t1 = MakeTriple("t1", "e1", "created", "e2");
        var t2 = MakeTriple("t2", "e2", "runs_on", "k3s");

        Assert.AreEqual(
            DreamService.GraphFingerprint([e1, e2], [t1, t2]),
            DreamService.GraphFingerprint([e2, e1], [t2, t1]));
    }

    [TestMethod]
    public void GraphFingerprint_NewReference_ChangesHash()
    {
        var before = MakeEntity("e1", "Rocky");
        var after = before with { LastReferencedAt = Now };

        Assert.AreNotEqual(
            DreamService.GraphFingerprint([before], []),
            DreamService.GraphFingerprint([after], []),
            "A reference is real activity and feeds the staleness-pruning directive.");
    }

    [TestMethod]
    public void MemoryCorpusFingerprint_IgnoresImportanceDrift()
    {
        // The importance decay pass rewrites ImportanceScore on essentially every cycle. If the
        // fingerprint tracked it, nothing would ever be unchanged and the gate would never fire.
        var before = MakeEntry("m1", "the sky is blue", importance: 0.5f);
        var after = before with { ImportanceScore = 0.47f };

        Assert.AreEqual(
            DreamService.MemoryCorpusFingerprint([before]),
            DreamService.MemoryCorpusFingerprint([after]));
    }

    [TestMethod]
    public void MemoryCorpusFingerprint_ContentEdit_ChangesHash()
    {
        var before = MakeEntry("m1", "the sky is blue");
        var after = before with { Content = "the sky is grey" };

        Assert.AreNotEqual(
            DreamService.MemoryCorpusFingerprint([before]),
            DreamService.MemoryCorpusFingerprint([after]));
    }

    [TestMethod]
    public void CorpusFingerprint_FieldBoundariesAreNotAmbiguous()
    {
        // Without a separator, ["ab","c"] and ["a","bc"] hash identically — two different
        // corpora reading as unchanged.
        Assert.AreNotEqual(
            DreamService.CorpusFingerprint(["ab", "c"]),
            DreamService.CorpusFingerprint(["a", "bc"]));
    }

    // -- Event watermarks -----------------------------------------------------

    [TestMethod]
    public void EventWatermark_IgnoresOldEventsAgingOutOfTheWindow()
    {
        // The load-bearing property. These passes mine a window relative to now, so hashing the
        // window's contents makes them re-run on an agent that generated no new events -- which is
        // how sequence skill detection came to manufacture a fresh pair of near-duplicate skills
        // twice a day from an unchanging 14-day tool-call log.
        var newest = Now;
        var full = new[] { Now.AddDays(-13), Now.AddDays(-5), newest };
        var drained = new[] { Now.AddDays(-5), newest };

        Assert.AreEqual(
            DreamService.EventWatermarkFingerprint(full),
            DreamService.EventWatermarkFingerprint(drained),
            "Losing the oldest event must not read as a change.");
    }

    [TestMethod]
    public void EventWatermark_AdvancesOnANewEvent()
    {
        var before = new[] { Now.AddDays(-5), Now.AddHours(-1) };
        var after = before.Append(Now).ToArray();

        Assert.AreNotEqual(
            DreamService.EventWatermarkFingerprint(before),
            DreamService.EventWatermarkFingerprint(after),
            "Real activity must re-open the pass.");
    }

    [TestMethod]
    public void EventWatermark_IsOrderIndependentAndTimezoneStable()
    {
        var a = new[] { Now.AddDays(-2), Now, Now.AddDays(-9) };
        var b = new[] { Now.ToOffset(TimeSpan.FromHours(9)), Now.AddDays(-9), Now.AddDays(-2) };

        Assert.AreEqual(
            DreamService.EventWatermarkFingerprint(a),
            DreamService.EventWatermarkFingerprint(b));
    }

    [TestMethod]
    public void EventWatermark_EmptyLogIsStable()
    {
        Assert.AreEqual(
            DreamService.EventWatermarkFingerprint([]),
            DreamService.EventWatermarkFingerprint([]));
    }

    // -- Fingerprints must cover inputs, never the pass's own output ----------

    [TestMethod]
    public void MemoryCorpusFingerprint_RewritingAnEntry_ChangesTheHash()
    {
        // Why identity reflection may not hash its own identity entries: it rewrites them on
        // essentially every run, so a fingerprint covering them is guaranteed to differ from the
        // one just stamped and the gate can never fire. Measured on a live agent: 5 gated passes,
        // only 1 skipped, because 4 of them perturbed their own input.
        var before = MakeEntry("i1", "I am a careful assistant.");
        var after = before with { Content = "I am a careful, curious assistant." };

        Assert.AreNotEqual(
            DreamService.MemoryCorpusFingerprint([before]),
            DreamService.MemoryCorpusFingerprint([after]),
            "A rewritten entry is a changed corpus -- which is exactly why outputs stay out of the hash.");
    }

    // -- Consolidation minimum interval ---------------------------------------
    //
    // Consolidation reuses the ledger with a constant fingerprint. That collapses ShouldSkip to
    // its time floor, which is exactly the semantics the pass wants — it is not gating on whether
    // the corpus changed, it is refusing to run twice in one afternoon because the pod restarted
    // twice. Reusing the ledger also means the interval survives those restarts.

    [TestMethod]
    public void ConsolidationSentinel_SkipsWhileTheLastRunIsYoungerThanTheInterval()
    {
        var record = new DreamPassLedger.PassRecord(DreamService.ConsolidationLedgerFingerprint, Now.AddHours(-2));

        Assert.IsTrue(DreamPassLedger.ShouldSkip(
            record, DreamService.ConsolidationLedgerFingerprint, Now, TimeSpan.FromHours(6)));
    }

    [TestMethod]
    public void ConsolidationSentinel_RunsOnceTheIntervalHasElapsed()
    {
        var record = new DreamPassLedger.PassRecord(DreamService.ConsolidationLedgerFingerprint, Now.AddHours(-7));

        Assert.IsFalse(DreamPassLedger.ShouldSkip(
            record, DreamService.ConsolidationLedgerFingerprint, Now, TimeSpan.FromHours(6)));
    }

    [TestMethod]
    public void ConsolidationSentinel_WithAZeroInterval_AlwaysRuns()
    {
        // Zero disables the floor. The ledger's own semantics for a non-positive interval are the
        // opposite — skip forever on an unchanged fingerprint — so the pass checks the option
        // before consulting the ledger at all. This asserts the shape the caller relies on.
        var record = new DreamPassLedger.PassRecord(DreamService.ConsolidationLedgerFingerprint, Now.AddMinutes(-1));

        Assert.IsTrue(DreamPassLedger.ShouldSkip(
            record, DreamService.ConsolidationLedgerFingerprint, Now, TimeSpan.Zero),
            "Guarding on ConsolidationMinInterval > Zero in the caller is what makes zero mean 'always run'.");
    }

    [TestMethod]
    public void ConsolidationSentinel_NeverRun_DoesNotSkip()
    {
        Assert.IsFalse(DreamPassLedger.ShouldSkip(
            null, DreamService.ConsolidationLedgerFingerprint, Now, TimeSpan.FromHours(6)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rockbot-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Skill MakeSkill(string name, string content) =>
        new(name, Summary: $"summary of {name}", Content: content, CreatedAt: Now.AddDays(-30));

    private static KnowledgeEntity MakeEntity(string id, string name) =>
        new(id, name, KnowledgeEntityType.Person, [], null, Now.AddDays(-30));

    private static KnowledgeTriple MakeTriple(string id, string s, string p, string o) =>
        new(id, s, p, o, 0.9f, null, Now.AddDays(-30));

    private static MemoryEntry MakeEntry(string id, string content, float importance = 0.5f) =>
        new(id, content, Category: "general", Tags: [], CreatedAt: Now.AddDays(-30),
            UpdatedAt: Now.AddDays(-30), Metadata: null, ImportanceScore: importance);
}
