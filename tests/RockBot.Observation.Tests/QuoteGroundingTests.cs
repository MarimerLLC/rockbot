namespace RockBot.Observation.Tests;

[TestClass]
public class QuoteGroundingTests
{
    private static TranscriptTurn Turn(string id, string content) =>
        new("conv1", id, "user", "user", content, DateTimeOffset.UtcNow);

    [TestMethod]
    public void Filter_QuoteSubstringPresent_KeepsObservation()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "I really prefer terse responses, no trailing summaries please."),
        };
        var proposal = new ProposedObservation(
            "User prefers terse responses.",
            "conv1", "t1",
            "prefer terse responses, no trailing");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(proposal, result[0]);
    }

    [TestMethod]
    public void Filter_QuoteWithDifferentWhitespace_StillMatches()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "I really prefer\n  terse   responses, no trailing summaries please."),
        };
        var proposal = new ProposedObservation(
            "User prefers terse responses.",
            "conv1", "t1",
            "prefer terse responses,    no\ttrailing");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(1, result.Count, "Whitespace differences must not block grounding");
    }

    [TestMethod]
    public void Filter_QuoteNotInTurn_DropsObservation()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "I really prefer terse responses."),
        };
        var proposal = new ProposedObservation(
            "User loves long detailed essays.",
            "conv1", "t1",
            "loves long detailed essays");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Filter_TurnIdNotFound_DropsObservation()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "Some content here."),
        };
        var proposal = new ProposedObservation(
            "Claim", "conv1", "t999", "Some content here.");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Filter_QuoteTooShort_DropsObservation()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "okay"),
        };
        var proposal = new ProposedObservation(
            "Claim", "conv1", "t1", "okay");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(0, result.Count, "Quotes shorter than the minimum carry no evidentiary weight");
    }

    [TestMethod]
    public void Filter_MixedValidAndInvalid_KeepsOnlyValid()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "I prefer terse responses, no trailing summaries please."),
            Turn("t2", "Actually let me elaborate further on that."),
        };
        var proposals = new[]
        {
            // Valid
            new ProposedObservation("A", "conv1", "t1", "prefer terse responses"),
            // Quote not in turn t2
            new ProposedObservation("B", "conv1", "t2", "loves brevity"),
            // Bogus turn id
            new ProposedObservation("C", "conv1", "t999", "anything"),
            // Valid, on t2
            new ProposedObservation("D", "conv1", "t2", "let me elaborate further"),
        };

        var result = QuoteGrounding.Filter(proposals, turns)
            .Select(p => p.Text)
            .OrderBy(s => s)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "A", "D" }, result);
    }

    [TestMethod]
    public void Filter_CaseInsensitive()
    {
        var turns = new List<TranscriptTurn>
        {
            Turn("t1", "I PREFER TERSE RESPONSES, no trailing summaries."),
        };
        var proposal = new ProposedObservation(
            "User prefers terse responses.",
            "conv1", "t1",
            "prefer terse responses");

        var result = QuoteGrounding.Filter(new[] { proposal }, turns).ToList();

        Assert.AreEqual(1, result.Count);
    }
}
