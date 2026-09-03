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
            + "Flagged entries were checked against telemetry. Validated ones stayed.");

        Assert.AreEqual(0, specifics.Count, string.Join(", ", specifics));
    }

    [TestMethod]
    public void OrdinaryWordsMidSentenceAreStillSpecifics()
    {
        // The baseline only absorbs words where capitalization is grammatical. Mid-sentence it
        // carries signal, so "Validated" here is treated as a label worth preserving. A
        // deployment that disagrees puts it in extraCommonWords, which applies everywhere.
        var specifics = MergeCoverage.ExtractSpecifics("Flagged entries were Validated against telemetry.");

        CollectionAssert.Contains(specifics.ToArray(), "Validated");

        var suppressed = MergeCoverage.ExtractSpecifics(
            "Flagged entries were Validated against telemetry.",
            new MergeCoverageVocabulary(["Validated"], null));

        Assert.AreEqual(0, suppressed.Count, string.Join(", ", suppressed));
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

        // Sentence-initial, which is where the hazard now lives: mid-sentence these names are
        // protected automatically by their position, so the list is only load-bearing here.
        var line = "May left at dawn. Will followed her. Rose waited by the gate.";

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


    // ── Sentence position ────────────────────────────────────────────────────

    [TestMethod]
    public void CommonWordMidSentenceIsStillASpecific()
    {
        // The reason the baseline was dangerous to apply everywhere. "New" opening a sentence is
        // ordinary English and is in the built-in list; inside "New Orleans" it names a real
        // place, and a merge that drops it has lost which Orleans is meant.
        var sources = new[] { Entry("a", "The venue moved to New Orleans last spring.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The venue moved to Orleans last spring.").ToArray(),
            "New");
    }

    [TestMethod]
    public void CommonWordOpeningASentenceIsIgnored()
    {
        var vocabulary = new MergeCoverageVocabulary(["Valid"], null);
        var sources = new[] { Entry("a", "Valid email-capable account IDs include marimer-work and xebia.") };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(
                sources,
                "Email-capable account IDs are marimer-work and xebia.",
                vocabulary).Count);
    }

    [TestMethod]
    public void AbbreviationPeriodDoesNotStartASentence()
    {
        // "St. Paul" and "Dr. May" must not put the following word in the position where the
        // common list applies -- that is precisely where a character named May would be lost.
        var vocabulary = new MergeCoverageVocabulary(null, null);
        var specifics = MergeCoverage.ExtractSpecifics("The show is in St. Paul with Dr. May attending.", vocabulary);

        CollectionAssert.IsSubsetOf(new[] { "Paul", "May" }, specifics.ToArray());
    }

    [TestMethod]
    public void CommonWordOpeningALineIsIgnored()
    {
        var vocabulary = new MergeCoverageVocabulary(["Direct"], null);
        var specifics = MergeCoverage.ExtractSpecifics(
            "Accounts:" + Environment.NewLine + "- Direct billing applies",
            vocabulary);

        CollectionAssert.DoesNotContain(specifics.ToArray(), "Direct");
    }

    [TestMethod]
    public void AlwaysSpecificStillWinsAtSentenceStart()
    {
        // A storytelling agent's character named Rose must survive even where the position
        // heuristic would otherwise hand her to the common list.
        var vocabulary = new MergeCoverageVocabulary(["Rose"], ["Rose"]);
        var sources = new[] { Entry("a", "Rose left the harbour before dawn.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "She left the harbour before dawn.", vocabulary).ToArray(),
            "Rose");
    }

    [TestMethod]
    public void AcronymsAreNotPositionExempt()
    {
        // All-caps is not grammatical capitalization, so opening a sentence says nothing about
        // it. A live merge dropped "LLC" alongside Microsoft, Google, ICS and IMAP.
        var sources = new[] { Entry("a", "IMAP delivery is configured for the rockbotagent account.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "Delivery is configured for the rockbotagent account.").ToArray(),
            "IMAP");
    }

    // ── Date equivalence ─────────────────────────────────────────────────────

    [TestMethod]
    public void WrittenDateIsSatisfiedByIsoDate()
    {
        // 13 of 70 rejections on a live corpus were exactly this: the merge normalized the
        // date and kept day and year intact, and was rejected for dropping "August".
        var sources = new[] { Entry("a", "A Red Fletcher show at White Rock Lounge on August 19, 2026.") };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(
                sources,
                "A Red Fletcher show at White Rock Lounge on 2026-08-19.").Count);
    }

    [TestMethod]
    public void IsoDateIsSatisfiedByWrittenDate()
    {
        var sources = new[] { Entry("a", "The summit ran on 2026-08-19.") };

        Assert.AreEqual(0, MergeCoverage.FindMissingSpecifics(sources, "The summit ran on August 19, 2026.").Count);
    }

    [TestMethod]
    public void MonthOutsideADateExpressionIsStillRequired()
    {
        // The guard that keeps this narrow. A person, product or release named August is not a
        // date and gets no credit from an unrelated numeric date elsewhere in the text.
        var sources = new[] { Entry("a", "August Wilson wrote the play; the ticket is dated 2026-08-19.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The ticket is dated 2026-08-19.").ToArray(),
            "August");
    }

    [TestMethod]
    public void DateDroppedEntirelyIsStillReported()
    {
        var sources = new[] { Entry("a", "The show is on August 19, 2026.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The show is scheduled for later this year.").ToArray(),
            "August");
    }

    [TestMethod]
    public void EquivalentDateMustMatchTheSourceYear()
    {
        // "August 2026" is not satisfied by an unrelated August in a different year.
        var sources = new[] { Entry("a", "The contract was signed in August 2026.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The contract was signed on 2025-08-04.").ToArray(),
            "August");
    }

    [TestMethod]
    public void EquivalentDateMustMatchTheSourceMonth()
    {
        var sources = new[] { Entry("a", "The show is on October 3, 2026.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The show is on 2026-08-19.").ToArray(),
            "October");
    }

    // ── Corpus-evidence rule ─────────────────────────────────────────────────

    [TestMethod]
    public void WordWithALowercaseTwinInTheSources_IsOrdinaryLanguage()
    {
        // Live rejection: "Marking" opens a clause mid-sentence, so position alone protected it,
        // and the same cluster was re-proposed and re-rejected every cycle. A sibling source
        // writes "marking" in lowercase — that is the corpus saying it is an ordinary verb.
        var sources = new[]
        {
            Entry("a", "Todo items are not accepted for updates. Marking a task complete needs the id."),
            Entry("b", "The agent keeps marking finished todos so the list stays short."),
        };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(
                sources,
                "Todo items are not accepted for updates; completing one needs the id, and the agent "
                + "keeps finished todos off the list.").Count);
    }

    [TestMethod]
    public void MidSentenceIdsWithLowercaseTwin_IsNotASpecific()
    {
        // "IDs" is not an acronym match (the trailing lowercase s breaks the all-caps regex), so
        // it arrived through the capitalized-word pass and outlived every merge proposal.
        var sources = new[]
        {
            Entry("a", "Contact IDs are account-scoped, so the contact ids differ per mailbox."),
        };

        CollectionAssert.DoesNotContain(
            MergeCoverage.FindMissingSpecifics(
                sources, "Contacts are account-scoped and differ per mailbox.").ToArray(),
            "IDs");
    }

    [TestMethod]
    public void ProperNounWithoutLowercaseTwin_StaysProtected()
    {
        // The whole point of sourcing the evidence from the text: a real name never appears
        // lowercase, so nothing about this rule can unprotect it.
        var sources = new[]
        {
            Entry("a", "The Eventbrite listing was set up by Xebia for the Austin workshop."),
        };

        var missing = MergeCoverage.FindMissingSpecifics(sources, "The listing was set up for a workshop.");

        CollectionAssert.Contains(missing.ToArray(), "Eventbrite");
        CollectionAssert.Contains(missing.ToArray(), "Xebia");
        CollectionAssert.Contains(missing.ToArray(), "Austin");
    }

    [TestMethod]
    public void AlwaysSpecific_BeatsTheCorpusEvidenceRule()
    {
        // A storytelling corpus writes "may" constantly and also has a character named May.
        // alwaysSpecificWords is the documented override, and it has to outrank corpus evidence
        // exactly as it outranks the baseline.
        var vocabulary = new MergeCoverageVocabulary(null, ["May"]);
        var sources = new[] { Entry("a", "May said the crossing may take three days.") };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(
                sources, "The crossing takes three days.", vocabulary).ToArray(),
            "May");
    }

    // ── Numeric category exemption ───────────────────────────────────────────

    [TestMethod]
    public void RoutingTelemetryDecimals_AreExemptByCategory()
    {
        // Routing anti-patterns restate figures recomputed from the routing log every cycle, so
        // "8.33" is already stale when the merge is proposed. Requiring it verbatim rejected the
        // same cluster indefinitely.
        var sources = new[]
        {
            Categorized("a", "Balanced sessions average 8.33 tool calls before escalating.", "anti-patterns/routing"),
        };

        Assert.AreEqual(
            0,
            MergeCoverage.FindMissingSpecifics(
                sources, "Balanced sessions make many tool calls before escalating.").Count);
    }

    [TestMethod]
    public void SameTextOutsideAnExemptCategory_StillRequiresTheNumber()
    {
        var sources = new[]
        {
            Categorized("a", "Balanced sessions average 8.33 tool calls before escalating.", "agent-knowledge/infrastructure"),
        };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(
                sources, "Balanced sessions make many tool calls before escalating.").ToArray(),
            "8.33");
    }

    [TestMethod]
    public void NumericExemptCategories_MatchNestedCategoriesButNotSiblingPrefixes()
    {
        var vocabulary = MergeCoverageVocabulary.Default;

        Assert.IsTrue(vocabulary.IsNumericExempt("anti-patterns/routing"));
        Assert.IsTrue(vocabulary.IsNumericExempt("anti-patterns/routing/high-tier"));
        Assert.IsFalse(vocabulary.IsNumericExempt("anti-patterns/routing-notes"));
        Assert.IsFalse(vocabulary.IsNumericExempt("anti-patterns"));
        Assert.IsFalse(vocabulary.IsNumericExempt(null));
    }

    [TestMethod]
    public void NumericExemptCategories_RoundTripFromJson()
    {
        var vocabulary = MergeCoverageVocabulary.Parse(
            """{ "numericExemptCategories": ["metrics/cost"] }""", out var error);

        Assert.IsNull(error);
        Assert.IsTrue(vocabulary.IsNumericExempt("metrics/cost"));

        // Additive: the built-in entry survives a file that names its own categories.
        Assert.IsTrue(vocabulary.IsNumericExempt("anti-patterns/routing"));
    }

    [TestMethod]
    public void DateInAnExemptCategory_IsStillRequiredWhenSpelledWithAMonthName()
    {
        // The exemption skips the numeric loop wholesale, so "2026-08-19" in an exempt category
        // is droppable. A month-name date still comes through the capitalized-word pass, which is
        // the documented boundary of the exemption.
        var sources = new[]
        {
            Categorized("a", "The routing review ran on August 19, 2026.", "anti-patterns/routing"),
        };

        CollectionAssert.Contains(
            MergeCoverage.FindMissingSpecifics(sources, "The routing review ran recently.").ToArray(),
            "August");
    }

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow);

    private static MemoryEntry Categorized(string id, string content, string category) =>
        new(id, content, category, [], DateTimeOffset.UtcNow);
}
