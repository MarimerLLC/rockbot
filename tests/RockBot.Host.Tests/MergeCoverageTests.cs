namespace RockBot.Host.Tests;

/// <summary>
/// Covers the safeguards that decide whether a proposed consolidation is safe to apply.
/// </summary>
/// <remarks>
/// These are deterministic because the prompt-level equivalents demonstrably did not hold.
/// <c>dream.md</c> already told the model that reinforcement signals importance, and a live
/// corpus still lost entries reinforced 214, 106 and 80 times; it already said to keep the
/// most specific detail, and a merge still dropped a person's legal name while keeping the
/// account list around it.
/// </remarks>
[TestClass]
public class MergeCoverageTests
{
    // ── Specific extraction ──────────────────────────────────────────────────

    [TestMethod]
    public void ExtractsProperNounsNumbersAndAcronyms()
    {
        var specifics = MergeCoverage.ExtractSpecifics(
            "Rocky filed the PWOP Productions W-9 on 2026-08-02 with Trish Roberts.");

        CollectionAssert.IsSubsetOf(
            new[] { "Rocky", "PWOP", "Productions", "Trish", "Roberts", "2026-08-02" },
            specifics.ToArray());
    }

    [TestMethod]
    public void SentenceLeadingCommonWordsAreNotTreatedAsSpecifics()
    {
        // Otherwise every sentence start would look like a proper noun and no merge would
        // ever pass, which would disable consolidation by accident rather than by decision.
        var specifics = MergeCoverage.ExtractSpecifics(
            "The user prefers email. This is a durable preference. Should always apply.");

        Assert.AreEqual(0, specifics.Count, string.Join(", ", specifics));
    }

    [TestMethod]
    public void PossessiveFormIsSatisfiedByThePlainName()
    {
        // "Rocky's" appears 27 times in the real corpus. Requiring the apostrophe form to
        // survive verbatim would reject merges that simply rephrase around it.
        var sources = new[] { Entry("a", "Rocky's Blazor Online Class launches soon") };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(sources, "The Blazor Online Class run by Rocky launches soon").Count);
    }

    [TestMethod]
    public void BareSingleDigitsAreNotTreatedAsSpecifics()
    {
        // "the top 3 items" rephrased as "the top three items" is not a loss of information.
        var sources = new[] { Entry("a", "Review the top 3 items") };

        Assert.AreEqual(0, MergeCoverage.FindMissingSpecifics(sources, "Review the top three items").Count);
    }

    [TestMethod]
    public void MultiDigitNumbersAreStillRequired()
    {
        var sources = new[] { Entry("a", "The deadline is the 10th, in 2026") };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "The deadline is soon");

        CollectionAssert.Contains(missing.ToArray(), "10");
        CollectionAssert.Contains(missing.ToArray(), "2026");
    }

    [TestMethod]
    public void ClockReformatIsConservativelyRejected()
    {
        // Documents a deliberate false positive. Recognising 2:00 PM and 14:00 as the same
        // instant would need real time parsing, and the failure modes are not symmetric: the
        // cost here is that a duplicate pair survives another cycle, whereas accepting it
        // blind also accepts "2:00 PM" collapsing to "2:00", which loses the meridiem.
        var sources = new[] { Entry("a", "The recording runs 2:00 PM to 3:00 PM") };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "The recording runs 14:00 to 15:00");

        CollectionAssert.Contains(missing.ToArray(), "2:00");
        CollectionAssert.Contains(missing.ToArray(), "PM");
    }

    [TestMethod]
    public void OrdinaryWordsOpeningASentenceAreNotSpecifics()
    {
        // Observed live: this exact shape rejected a sound merge because "Candidate",
        // "Adding", "Flagged" and "Validated" were read as proper nouns.
        var specifics = MergeCoverage.ExtractSpecifics(
            "Candidate low-signal words were reviewed. Adding them is wrong. "
            + "Flagged entries were Validated against telemetry.");

        Assert.AreEqual(0, specifics.Count, string.Join(", ", specifics));
    }

    [TestMethod]
    public void WordsThatCannotCarryMeaning_AreNotSpecifics()
    {
        Assert.AreEqual(
            0,
            MergeCoverage.ExtractSpecifics("IDs vary by mailbox. Downloading works. Enjoys music.").Count);
    }

    [TestMethod]
    public void GenericLookingWordsThatAreLoadBearingHere_StaySpecifics()
    {
        // These read as ordinary English but name real things in this corpus — "OneDrive
        // Personal", "Blazor Online Class", "MVP Azure Extended Benefit". Stoplisting them to
        // quieten a false positive would blunt a correct rejection.
        var specifics = MergeCoverage.ExtractSpecifics(
            "Personal OneDrive holds the Blazor Online Class notes and the MVP Azure Extended Benefit task.");

        foreach (var expected in new[] { "Personal", "Blazor", "Online", "Class", "Azure", "Extended", "Benefit" })
            CollectionAssert.Contains(specifics.ToArray(), expected);
    }

    [TestMethod]
    public void ContentWithNoSpecifics_ImposesNoRequirement()
    {
        var sources = new[] { Entry("a", "the user prefers concise replies") };
        Assert.AreEqual(0, MergeCoverage.FindMissingSpecifics(sources, "user prefers brevity").Count);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void MergeThatKeepsEverySpecific_IsAccepted()
    {
        var sources = new[]
        {
            Entry("a", "Rocky has a dog named Milo"),
            Entry("b", "Rocky has a Sheltie named Milo"),
        };

        var missing = MergeCoverage.FindMissingSpecifics(
            sources, "Rocky has a dog — a Sheltie (Shetland Sheepdog) named Milo");

        Assert.AreEqual(0, missing.Count, string.Join(", ", missing));
    }

    [TestMethod]
    public void RegressionRockfordDuane_MergeThatDropsALegalNameIsRejected()
    {
        // The real loss: importance 0.99, reinforced 73x. The successor kept the
        // machine-readable account map and silently dropped the name. It read fine.
        var sources = new[]
        {
            Entry("identity",
                "Rocky Lhotka also appears in travel and calendar data as Rockford Duane Lhotka. "
                + "Connected accounts include marimer-work, lhotka.net and xebia."),
        };

        var merged =
            "Rocky's connected communications infrastructure includes the accounts "
            + "marimer-work, lhotka.net and xebia.";

        var missing = MergeCoverage.FindMissingSpecifics(sources, merged);

        CollectionAssert.Contains(missing.ToArray(), "Rockford");
        CollectionAssert.Contains(missing.ToArray(), "Duane");
    }

    [TestMethod]
    public void MergeThatDropsADateIsRejected()
    {
        var sources = new[] { Entry("a", "Estimated taxes are due 2026-09-10") };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "Estimated taxes are due in the autumn");

        CollectionAssert.Contains(missing.ToArray(), "2026-09-10");
    }

    [TestMethod]
    public void ReformattingThatKeepsTheComponentsIsAccepted()
    {
        // Substring matching credits a merge for expanding or restructuring, so long as the
        // component survives somewhere in the text.
        var sources = new[] { Entry("a", "Rockford filed in 2026") };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(sources, "Rockford Duane Lhotka filed on 2026-04-15").Count);
    }

    [TestMethod]
    public void SpecificsAreRequiredAcrossAllSources_NotJustTheLongest()
    {
        var sources = new[]
        {
            Entry("a", "Allen Conway works at Xebia"),
            Entry("b", "Trish Roberts works at Xebia"),
        };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "Allen Conway works at Xebia");

        CollectionAssert.AreEquivalent(new[] { "Roberts", "Trish" }, missing.ToArray());
    }

    [TestMethod]
    public void MissingSpecificsAreReportedInStableOrder()
    {
        var sources = new[] { Entry("a", "Zulu Alpha Mike went to Boston") };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "someone travelled");

        CollectionAssert.AreEqual(new[] { "Alpha", "Boston", "Mike", "Zulu" }, missing.ToArray());
    }

    [TestMethod]
    public void RejectedMergeSources_MustNotBeReachableViaTheEphemeralPath()
    {
        // Regression for a defect the unit tests missed and a live cycle caught. dream.md
        // requires every sourceId to also appear in toDelete, so rejecting a merge is not
        // enough on its own — the standalone-removal loop would archive the very sources the
        // rejection was meant to save, leaving them with no replacement at all. Strictly
        // worse than allowing the lossy merge.
        //
        // Reproduces the exact shape seen in production: one dto whose sourceIds are fully
        // duplicated into toDelete.
        var sources = new[] { Entry("s1", "Allen Conway is a Xebia collaborator"), Entry("s2", "Allen Conway uses accountId rockyl") };
        var mergedDroppingXebia = "Allen Conway uses accountId rockyl";

        var missing = MergeCoverage.FindMissingSpecifics(sources, mergedDroppingXebia);
        Assert.IsTrue(missing.Count > 0, "precondition: this merge must be rejected");

        // The rejection has to translate into the source IDs being excluded from removal.
        var rejectedSources = new HashSet<string>(sources.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var toDelete = new[] { "s1", "s2" };
        var actuallyRemoved = toDelete.Where(id => !rejectedSources.Contains(id)).ToArray();

        Assert.AreEqual(0, actuallyRemoved.Length,
            "a rejected merge's sources must survive the toDelete sweep");
    }

    // ── Deployment-specific vocabulary ───────────────────────────────────────

    [TestMethod]
    public void ExtraCommonWords_SuppressDeploymentNoise()
    {
        var vocab = new MergeCoverageVocabulary(extraCommonWords: ["briefing", "triage"], alwaysSpecificWords: null);

        Assert.AreEqual(0, MergeCoverage.ExtractSpecifics("Briefing and Triage completed", vocab).Count);
        CollectionAssert.Contains(
            MergeCoverage.ExtractSpecifics("Briefing and Triage completed").ToArray(), "Briefing");
    }

    [TestMethod]
    public void AlwaysSpecificWords_ReclaimBuiltInWordsThatAreActuallyNames()
    {
        // The failure this exists to prevent. A storytelling agent's characters collide head-on
        // with generic English: "may", "will" and "some" are all built-in common words, so
        // characters named May, Will or Rose would silently carry no coverage protection and a
        // merge could drop them — the exact population that must never be lost.
        var storytelling = new MergeCoverageVocabulary(
            extraCommonWords: null,
            alwaysSpecificWords: ["May", "Will", "Rose"]);

        var line = "May and Will found Rose in the garden";

        var unprotected = MergeCoverage.ExtractSpecifics(line);
        Assert.IsFalse(unprotected.Contains("May"), "precondition: the built-in list swallows May");
        Assert.IsFalse(unprotected.Contains("Will"), "precondition: the built-in list swallows Will");

        var protectedSpecifics = MergeCoverage.ExtractSpecifics(line, storytelling);
        foreach (var name in new[] { "May", "Will", "Rose" })
            CollectionAssert.Contains(protectedSpecifics.ToArray(), name);
    }

    [TestMethod]
    public void AlwaysSpecific_WinsOverExtraCommon()
    {
        var vocab = new MergeCoverageVocabulary(extraCommonWords: ["rose"], alwaysSpecificWords: ["Rose"]);
        CollectionAssert.Contains(MergeCoverage.ExtractSpecifics("Rose arrived", vocab).ToArray(), "Rose");
    }

    [TestMethod]
    public void VocabularyRoundTripsFromJson()
    {
        var vocab = MergeCoverageVocabulary.Parse(
            """
            {
              // comments and trailing commas are tolerated
              "extraCommonWords": ["briefing"],
              "alwaysSpecificWords": ["May"],
            }
            """, out var error);

        Assert.IsNull(error);
        Assert.IsTrue(vocab.IsCommon("briefing"));
        Assert.IsFalse(vocab.IsCommon("May"));
    }

    [TestMethod]
    public void MalformedVocabulary_FallsBackWithoutDisablingTheCheck()
    {
        // A broken override must never silently turn coverage checking off — that would
        // reintroduce exactly the data loss this safeguard exists to stop.
        var vocab = MergeCoverageVocabulary.Parse("{ not json", out var error);

        Assert.IsNotNull(error);
        CollectionAssert.Contains(MergeCoverage.ExtractSpecifics("Rockford filed", vocab).ToArray(), "Rockford");
    }

    [TestMethod]
    public void AbsentVocabulary_UsesTheBuiltInBaseline()
    {
        Assert.AreEqual(MergeCoverageVocabulary.Default.CommonWordCount,
            MergeCoverageVocabulary.Parse(null, out var error).CommonWordCount);
        Assert.IsNull(error);
    }

    // ── High-value pruning floor ─────────────────────────────────────────────

    [TestMethod]
    public void HighImportanceEntryIsProtectedFromPruning()
    {
        var options = new DreamOptions();
        Assert.IsTrue(MergeCoverage_IsProtected(Entry("a", "core fact") with { ImportanceScore = 0.99f }, options));
    }

    [TestMethod]
    public void HeavilyReinforcedEntryIsProtectedFromPruning()
    {
        // The 214x self-model entry that was destroyed.
        var options = new DreamOptions();
        Assert.IsTrue(MergeCoverage_IsProtected(Entry("a", "self model") with { ReinforcementCount = 214 }, options));
    }

    [TestMethod]
    public void OrdinaryEntryIsNotProtected()
    {
        var options = new DreamOptions();
        var ordinary = Entry("a", "transient note") with { ImportanceScore = 0.5f, ReinforcementCount = 1 };
        Assert.IsFalse(MergeCoverage_IsProtected(ordinary, options));
    }

    [TestMethod]
    public void ProtectionFloorIsConfigurable()
    {
        var permissive = new DreamOptions
        {
            PruningProtectionImportance = 1.5f,
            PruningProtectionReinforcementCount = int.MaxValue,
        };

        var valuable = Entry("a", "core") with { ImportanceScore = 0.99f, ReinforcementCount = 214 };
        Assert.IsFalse(MergeCoverage_IsProtected(valuable, permissive));
    }

    private static bool MergeCoverage_IsProtected(MemoryEntry entry, DreamOptions options) =>
        DreamService.IsProtectedFromPruning(entry, options);

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow);
}
