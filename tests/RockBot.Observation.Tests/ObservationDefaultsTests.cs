using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationDefaultsTests
{
    private const string Base = "/data/agent";

    [TestMethod]
    public void CreateTheoryOfSelf_HasExpectedShape()
    {
        var t = ObservationDefaults.CreateTheoryOfSelf(Base);

        Assert.AreEqual(ObservationDefaults.TheoryOfSelfName, t.Name);
        Assert.AreSame(TranscriptFilters.Everything, t.Filter);
        Assert.AreEqual(DefaultPrompts.TheoryOfSelfExtraction, t.ExtractionPrompt);
        Assert.AreEqual(DefaultPrompts.DifferentialEvaluation, t.EvaluationPrompt);
        Assert.AreEqual(ModelTier.Low, t.ExtractionTier);
        Assert.AreEqual(ModelTier.Balanced, t.EvaluationTier);
        Assert.IsTrue(t.IncludeBehaviorSummary,
            "theory-of-self uses behaviour summary input (per design)");

        // Both files live under observation/ — these are inspection
        // artifacts, not auto-loaded agent-profile files. v1 is collect-only.
        StringAssert.EndsWith(t.OutputMarkdownPath,
            Path.Combine(Base, "observation", "theory-of-self.md"));
        StringAssert.EndsWith(t.StateFilePath,
            Path.Combine(Base, "observation", "theory-of-self.json"));
    }

    [TestMethod]
    public void CreateTheoryOfUser_HasExpectedShape()
    {
        var t = ObservationDefaults.CreateTheoryOfUser(Base);

        Assert.AreEqual(ObservationDefaults.TheoryOfUserName, t.Name);
        Assert.AreSame(TranscriptFilters.UserAuthored, t.Filter,
            "theory-of-user uses the user-authored filter, not everything");
        Assert.AreEqual(DefaultPrompts.TheoryOfUserExtraction, t.ExtractionPrompt);
        Assert.AreEqual(DefaultPrompts.DifferentialEvaluation, t.EvaluationPrompt);
        Assert.IsFalse(t.IncludeBehaviorSummary,
            "theory-of-user does not need behaviour summary input — it observes the user, not the agent");

        StringAssert.EndsWith(t.OutputMarkdownPath,
            Path.Combine(Base, "observation", "theory-of-user.md"));
        StringAssert.EndsWith(t.StateFilePath,
            Path.Combine(Base, "observation", "theory-of-user.json"));
    }

    [TestMethod]
    public void AddDefaultObservationTargets_RegistersBothTargetsAndCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotObservation();
        services.AddDefaultObservationTargets(Base);

        var provider = services.BuildServiceProvider();

        var targets = provider.GetServices<ObservationTarget>().ToList();
        Assert.AreEqual(2, targets.Count);
        Assert.IsTrue(targets.Any(t => t.Name == ObservationDefaults.TheoryOfSelfName));
        Assert.IsTrue(targets.Any(t => t.Name == ObservationDefaults.TheoryOfUserName));

        Assert.IsNotNull(provider.GetService<IObservationStateStore>());
    }

    [TestMethod]
    public void AddDefaultObservationTargets_NullPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddDefaultObservationTargets(""));
    }
}
