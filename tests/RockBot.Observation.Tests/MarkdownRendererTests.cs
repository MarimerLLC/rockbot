namespace RockBot.Observation.Tests;

[TestClass]
public class MarkdownRendererTests
{
    private static ObservationTarget Target() => new()
    {
        Name = "theory-of-self",
        Filter = new PassThrough(),
        ExtractionPrompt = "x",
        EvaluationPrompt = "x",
        StateFilePath = "/tmp/x.json",
        OutputMarkdownPath = "/tmp/x.md",
        PromotionThreshold = 3,
    };

    private static readonly DateTimeOffset Rendered = DateTimeOffset.Parse("2026-05-08T12:00:00Z");

    [TestMethod]
    public void Render_EmptyState_ShowsZeroTheoriesAndNoneCandidates()
    {
        var md = MarkdownRenderer.Render(Target(), new ObservationState(), Rendered);

        StringAssert.Contains(md, "# Theory of self");
        StringAssert.Contains(md, "## Theories (0)");
        StringAssert.Contains(md, "_No theories yet — observations are still accumulating._");
        StringAssert.Contains(md, "## Candidate observations (0)");
        StringAssert.Contains(md, "_(none)_");
        StringAssert.Contains(md, "Manual edits to this file will be overwritten");
    }

    [TestMethod]
    public void Render_WithTheoriesAndCandidates_ShowsCounts()
    {
        var ref1 = new ObservationReference("conv1", "t1", "verbatim quote here", DateTimeOffset.Parse("2026-05-01T10:00:00Z"));
        var ref2 = new ObservationReference("conv2", "t1", "another supporting quote", DateTimeOffset.Parse("2026-05-05T10:00:00Z"));

        var state = new ObservationState
        {
            Theories =
            {
                new Theory
                {
                    Id = "thry_001",
                    Text = "User prefers terse responses",
                    PromotedAt = DateTimeOffset.Parse("2026-04-15T00:00:00Z"),
                    LastReinforced = ref2.ObservedAt,
                    SourceCandidateIds = { "cand_orig" },
                    References = { ref1, ref2 },
                },
            },
            Candidates =
            {
                new Candidate
                {
                    Id = "cand_001",
                    Text = "Agent over-explores tool calls",
                    ClusterId = "c1",
                    Count = 2,
                    FirstSeen = DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                    LastSeen = DateTimeOffset.Parse("2026-05-07T00:00:00Z"),
                    References = { ref1, ref2 },
                },
            },
        };

        var md = MarkdownRenderer.Render(Target(), state, Rendered);

        StringAssert.Contains(md, "## Theories (1)");
        StringAssert.Contains(md, "### User prefers terse responses.");
        StringAssert.Contains(md, "Reinforced:** 2 conversation(s)");
        StringAssert.Contains(md, "First observed:** 2026-05-01");
        StringAssert.Contains(md, "Last reinforced:** 2026-05-05");
        StringAssert.Contains(md, "another supporting quote");

        StringAssert.Contains(md, "## Candidate observations (1)");
        StringAssert.Contains(md, "Threshold: 3 distinct conversation(s)");
        StringAssert.Contains(md, "**Agent over-explores tool calls**");
    }

    [TestMethod]
    public void Render_TheoriesOrderedByReferenceCount()
    {
        var refLight = new[] { new ObservationReference("conv1", "t1", "quote one here", Rendered) };
        var refHeavy = Enumerable.Range(0, 5)
            .Select(i => new ObservationReference($"conv{i}", "t1", "quote here please", Rendered.AddDays(-i)))
            .ToList();

        var state = new ObservationState
        {
            Theories =
            {
                new Theory
                {
                    Id = "lite",
                    Text = "Lightly reinforced",
                    PromotedAt = Rendered,
                    LastReinforced = Rendered,
                    References = refLight.ToList(),
                },
                new Theory
                {
                    Id = "heavy",
                    Text = "Heavily reinforced",
                    PromotedAt = Rendered,
                    LastReinforced = Rendered,
                    References = refHeavy.ToList(),
                },
            },
        };

        var md = MarkdownRenderer.Render(Target(), state, Rendered);

        var heavyIdx = md.IndexOf("Heavily reinforced", StringComparison.Ordinal);
        var liteIdx = md.IndexOf("Lightly reinforced", StringComparison.Ordinal);
        Assert.IsTrue(heavyIdx < liteIdx, "More-reinforced theories should appear first");
    }

    [TestMethod]
    public void Render_LongQuoteTruncated()
    {
        var longQuote = new string('a', 500);
        var state = new ObservationState
        {
            Theories =
            {
                new Theory
                {
                    Id = "t",
                    Text = "A theory",
                    PromotedAt = Rendered,
                    LastReinforced = Rendered,
                    References = { new ObservationReference("c", "t", longQuote, Rendered) },
                },
            },
        };

        var md = MarkdownRenderer.Render(Target(), state, Rendered);

        Assert.IsFalse(md.Contains(longQuote), "The full 500-char quote should not appear");
        StringAssert.Contains(md, "…", "Truncation marker should be present");
    }

    [TestMethod]
    public void Render_TitleFormattedFromKebabName()
    {
        var t = Target();
        // theory-of-user
        var custom = new ObservationTarget
        {
            Name = "theory-of-user",
            Filter = new PassThrough(),
            ExtractionPrompt = "x",
            EvaluationPrompt = "x",
            StateFilePath = "/tmp/x.json",
            OutputMarkdownPath = "/tmp/x.md",
        };
        var md = MarkdownRenderer.Render(custom, new ObservationState(), Rendered);
        StringAssert.Contains(md, "# Theory of user");
    }

    [TestMethod]
    public void Render_IncludesGeneratedTimestamp()
    {
        var md = MarkdownRenderer.Render(Target(), new ObservationState(), Rendered);
        StringAssert.Contains(md, "2026-05-08 12:00 UTC");
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }
}
