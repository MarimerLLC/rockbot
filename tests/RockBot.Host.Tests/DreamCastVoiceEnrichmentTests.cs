using Microsoft.Extensions.Configuration;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the cast voice enrichment pass. The pass exists because character dialogue flattens
/// over time for a reason no amount of prompting fixes: a character's establishing lines scroll
/// out of the context window, the record keeps only their face and their actions, and the next
/// appearance is written from a physical description. Memory mining cannot close that gap — it
/// only ever reads the session that just ended.
/// </summary>
[TestClass]
public class DreamCastVoiceEnrichmentTests
{
    private static DreamOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var opts = new DreamOptions();
        config.GetSection("Dream").Bind(opts);
        return opts;
    }

    [TestMethod]
    public void DefaultsToDisabled_SoOnlyCastKeepingAgentsOptIn()
    {
        var opts = new DreamOptions();

        Assert.IsFalse(opts.CastVoiceEnrichmentEnabled);
        Assert.AreEqual(string.Empty, opts.CastVoiceCategory);
        Assert.AreEqual("VOICE CARD", opts.CastVoiceMarker);
        Assert.AreEqual("cast-voice-dream.md", opts.CastVoiceDirectivePath);
        Assert.AreEqual(12, opts.CastVoiceMaxPerCycle);
    }

    [TestMethod]
    public void WithNoCategoryConfigured_ThePassHasNothingToReadAndIsInert()
    {
        // The category name is deployment-specific and deliberately has no default, so a
        // deployment that flips the flag without naming a category gets a no-op rather than
        // a pass reading some arbitrary category.
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:CastVoiceEnrichmentEnabled"] = "true",
        });

        Assert.IsTrue(opts.CastVoiceEnrichmentEnabled);
        Assert.AreEqual(string.Empty, opts.CastVoiceCategory);
    }

    [TestMethod]
    public void Binds_FromEnvironmentStyleConfig()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:CastVoiceEnrichmentEnabled"] = "true",
            ["Dream:CastVoiceCategory"] = "story/people",
            ["Dream:CastVoiceMarker"] = "SPEECH",
            ["Dream:CastVoiceMaxPerCycle"] = "3",
        });

        Assert.IsTrue(opts.CastVoiceEnrichmentEnabled);
        Assert.AreEqual("story/people", opts.CastVoiceCategory);
        Assert.AreEqual("SPEECH", opts.CastVoiceMarker);
        Assert.AreEqual(3, opts.CastVoiceMaxPerCycle);
    }

    [TestMethod]
    public void IsIndependentOfMemoryConsolidation()
    {
        // The pass has to be usable on an agent that deliberately runs with consolidation off,
        // which is exactly the configuration where voices were being lost.
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:CastVoiceEnrichmentEnabled"] = "true",
            ["Dream:MemoryConsolidationEnabled"] = "false",
        });

        Assert.IsTrue(opts.CastVoiceEnrichmentEnabled);
        Assert.IsFalse(opts.MemoryConsolidationEnabled);
    }

    // ── MergeVoiceCard ────────────────────────────────────────────────────────

    [TestMethod]
    public void MergeVoiceCard_PreservesTheOriginalEntryVerbatim()
    {
        // The whole safety property of an in-place pass: the recorded facts survive whatever
        // the model returns.
        const string original = "Alder drives the pre-dawn delivery round and keeps the depot keys.";

        var merged = DreamService.MergeVoiceCard(
            original, "VOICE CARD", "Alder (the delivery round)", "Rural, medium sentences, never contracts.");

        StringAssert.StartsWith(merged, original);
        StringAssert.Contains(merged, "VOICE CARD - Alder (the delivery round).");
        StringAssert.Contains(merged, "never contracts");
    }

    [TestMethod]
    public void MergeVoiceCard_SeparatesCardFromOriginalWithABlankLine()
    {
        var merged = DreamService.MergeVoiceCard("Brandt runs the kitchen.", "VOICE CARD", "Brandt", "Flat and regional.");

        StringAssert.Contains(merged, "Brandt runs the kitchen.\n\nVOICE CARD - Brandt.");
    }

    [TestMethod]
    public void MergeVoiceCard_WithNoExistingContent_ReturnsTheCardAlone()
    {
        var merged = DreamService.MergeVoiceCard(null, "VOICE CARD", "Coyne", "Short sentences, heavy contractions.");

        Assert.AreEqual("VOICE CARD - Coyne. Short sentences, heavy contractions.", merged);
    }

    [TestMethod]
    public void MergeVoiceCard_WithNoCharacterKey_StillProducesAMarkedCard()
    {
        var merged = DreamService.MergeVoiceCard("Vance works the ticket window.", "VOICE CARD", "   ", "Calm and immovable.");

        StringAssert.Contains(merged, "VOICE CARD. Calm and immovable.");
        StringAssert.StartsWith(merged, "Vance works the ticket window.");
    }

    [TestMethod]
    public void MergeVoiceCard_OutputIsDetectedByHasVoiceMarker_SoThePassConverges()
    {
        // A merged entry must be recognisable as done, or the pass re-enriches the same
        // character every cycle and never terminates.
        var merged = DreamService.MergeVoiceCard("Halloran repairs clocks.", "VOICE CARD", "Halloran", "Formal, never contracts.");

        Assert.IsTrue(DreamService.HasVoiceMarker(merged, "VOICE CARD"));
    }

    // ── HasVoiceMarker ────────────────────────────────────────────────────────

    [TestMethod]
    public void HasVoiceMarker_IsCaseInsensitive()
    {
        Assert.IsTrue(DreamService.HasVoiceMarker("voice card - Coyne. Talks constantly.", "VOICE CARD"));
    }

    [TestMethod]
    public void HasVoiceMarker_FalseForPlainCastEntries()
    {
        Assert.IsFalse(DreamService.HasVoiceMarker("Vance is six-four and wears a grey coat.", "VOICE CARD"));
    }

    [TestMethod]
    public void HasVoiceMarker_FalseForEmptyInputs()
    {
        Assert.IsFalse(DreamService.HasVoiceMarker(null, "VOICE CARD"));
        Assert.IsFalse(DreamService.HasVoiceMarker("", "VOICE CARD"));
        Assert.IsFalse(DreamService.HasVoiceMarker("anything at all", ""));
    }

    // -- Activity gate ---------------------------------------------------------

    [TestMethod]
    public void RequiresRecentActivity_DefaultsOn_SoAnIdleAgentStopsPaying()
    {
        // "Some character still lacks a card" stays true for months, so on its own it kept the
        // pass billing a full-corpus call twice a day to invent voices for a cast nobody had
        // played with. Voices are worth writing for the characters who just walked on stage.
        Assert.IsTrue(new DreamOptions().CastVoiceRequiresRecentActivity);
    }

    [TestMethod]
    public void RequiresRecentActivity_CanBeTurnedOffFromConfiguration()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:CastVoiceRequiresRecentActivity"] = "false",
        });

        Assert.IsFalse(opts.CastVoiceRequiresRecentActivity);
    }

    // -- ExtractVoiceCardLine --------------------------------------------------

    [TestMethod]
    public void ExtractVoiceCardLine_ReturnsOnlyTheCard_NotTheWholeEntry()
    {
        // The prompt lists voices already in use so the model keeps them distinct. Shipping the
        // finished entries instead is what invited proposals against characters already done.
        var entry = DreamService.MergeVoiceCard(
            "Brandt runs the kitchen and shouts at the pass.",
            "VOICE CARD", "Brandt", "Flat and regional. Short sentences.");

        var line = DreamService.ExtractVoiceCardLine(entry, "VOICE CARD");

        StringAssert.StartsWith(line, "VOICE CARD - Brandt.");
        Assert.IsFalse(line.Contains("shouts at the pass"), "The entry body must not leak into the voices list.");
    }

    [TestMethod]
    public void ExtractVoiceCardLine_StopsAtABlankLine_ForHandEditedEntries()
    {
        var entry = string.Join('\n',
            "Coyne tends bar.", "", "VOICE CARD - Coyne. Talks constantly.", "", "Added by hand later.");

        var line = DreamService.ExtractVoiceCardLine(entry, "VOICE CARD");

        Assert.AreEqual("VOICE CARD - Coyne. Talks constantly.", line);
    }

    [TestMethod]
    public void ExtractVoiceCardLine_FlattensAMultiLineCardToOneLine()
    {
        var entry = string.Join('\n',
            "Vance works the window.", "", "VOICE CARD - Vance. Calm.", "Never contracts.");

        var line = DreamService.ExtractVoiceCardLine(entry, "VOICE CARD");

        Assert.AreEqual("VOICE CARD - Vance. Calm. Never contracts.", line);
    }

    [TestMethod]
    public void ExtractVoiceCardLine_EmptyForUncardedOrBlankEntries()
    {
        Assert.AreEqual(string.Empty, DreamService.ExtractVoiceCardLine("Vance wears a grey coat.", "VOICE CARD"));
        Assert.AreEqual(string.Empty, DreamService.ExtractVoiceCardLine(null, "VOICE CARD"));
        Assert.AreEqual(string.Empty, DreamService.ExtractVoiceCardLine("anything", ""));
    }
}
