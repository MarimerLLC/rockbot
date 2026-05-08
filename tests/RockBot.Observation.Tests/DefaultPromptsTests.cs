namespace RockBot.Observation.Tests;

[TestClass]
public class DefaultPromptsTests
{
    [TestMethod]
    public void TheoryOfSelfExtraction_NonEmptyAndContainsCoreRules()
    {
        var p = DefaultPrompts.TheoryOfSelfExtraction;
        Assert.IsFalse(string.IsNullOrWhiteSpace(p));
        StringAssert.Contains(p, "BEHAVIOR ONLY",
            "Behavior-only-not-motivation is the load-bearing anti-hallucination rule");
        StringAssert.Contains(p, "QUOTE EVIDENCE",
            "Quote evidence is required by the framework's grounding pipeline");
        StringAssert.Contains(p, "agent",
            "Theory-of-self prompt should explicitly orient toward the agent");
    }

    [TestMethod]
    public void TheoryOfUserExtraction_NonEmptyAndContainsCoreRules()
    {
        var p = DefaultPrompts.TheoryOfUserExtraction;
        Assert.IsFalse(string.IsNullOrWhiteSpace(p));
        StringAssert.Contains(p, "BEHAVIOR ONLY");
        StringAssert.Contains(p, "QUOTE EVIDENCE");
        StringAssert.Contains(p, "user",
            "Theory-of-user prompt should explicitly orient toward the user");
    }

    [TestMethod]
    public void DifferentialEvaluation_DescribesAllThreeVerdicts()
    {
        var p = DefaultPrompts.DifferentialEvaluation;
        Assert.IsFalse(string.IsNullOrWhiteSpace(p));
        StringAssert.Contains(p, "promote");
        StringAssert.Contains(p, "refine");
        StringAssert.Contains(p, "reject");
        StringAssert.Contains(p, "prefer reject",
            "Evaluator should err on the side of rejection given context-influence stakes");
    }

    [TestMethod]
    public void Prompts_AreDistinctTexts()
    {
        // Cheap sanity check that we didn't accidentally copy/paste the same
        // body across the three constants.
        Assert.AreNotEqual(DefaultPrompts.TheoryOfSelfExtraction, DefaultPrompts.TheoryOfUserExtraction);
        Assert.AreNotEqual(DefaultPrompts.TheoryOfSelfExtraction, DefaultPrompts.DifferentialEvaluation);
        Assert.AreNotEqual(DefaultPrompts.TheoryOfUserExtraction, DefaultPrompts.DifferentialEvaluation);
    }
}
