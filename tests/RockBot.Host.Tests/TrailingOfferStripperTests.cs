using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// Trailing-offer stripping: the regex finds the candidate, the decision model confirms it is only
/// optional help before it is removed, and stripping proceeds as before when the model can't answer.
/// </summary>
[TestClass]
public class TrailingOfferStripperTests
{
    private const string Body =
        "I found two open slots with Bob on Thursday: 3pm and 4pm. Both are 30 minutes and neither conflicts " +
        "with your existing meetings or the travel block on Friday morning.";
    private const string NeededQuestion = "\n\nShould I book 3pm or 4pm?";
    private const string Throwaway = "\n\nLet me know if you'd like me to draft an agenda too.";

    [TestMethod]
    public async Task NoCandidate_NoCall()
    {
        var decisions = new FakeDecisionService(0.99);

        Assert.AreEqual(Body, await new TrailingOfferStripper(decisions).StripAsync(Body));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public async Task ModelSaysNeededQuestion_Kept()
    {
        var decisions = new FakeDecisionService(0.03);

        Assert.AreEqual(Body + NeededQuestion, await new TrailingOfferStripper(decisions).StripAsync(Body + NeededQuestion));
        var state = (IDictionary<string, object?>)decisions.Requests.Single().State;
        Assert.AreEqual("Should I book 3pm or 4pm?", state["trailing_paragraph"]);
        Assert.AreEqual(DecisionPurposes.TrailingOffer, decisions.Requests.Single().Purpose);
    }

    [TestMethod]
    public async Task ModelSaysOptionalOffer_Stripped()
    {
        Assert.AreEqual(Body, await new TrailingOfferStripper(new FakeDecisionService(0.97)).StripAsync(Body + Throwaway));
    }

    [TestMethod]
    public async Task ModelUnavailableOrUnconfigured_StripsAsBefore()
    {
        Assert.AreEqual(Body, await new TrailingOfferStripper(new FakeDecisionService(null)).StripAsync(Body + NeededQuestion));
        Assert.AreEqual(Body, await new TrailingOfferStripper().StripAsync(Body + NeededQuestion));
        Assert.AreEqual(Body, ResponseSanitizer.StripTrailingOffers(Body + Throwaway), "sync path unchanged");
    }

    [TestMethod]
    public void FindTrailingOffer_KeepsTheSubstanceGuard()
    {
        Assert.IsNull(ResponseSanitizer.FindTrailingOffer("Done.\n\nWould you like me to explain each of the twelve steps in detail?"),
            "stripping would remove most of the reply");
    }
}
