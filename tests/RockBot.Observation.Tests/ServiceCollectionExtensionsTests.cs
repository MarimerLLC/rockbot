using Microsoft.Extensions.DependencyInjection;

namespace RockBot.Observation.Tests;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddRockBotObservation_RegistersStateStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotObservation();

        var provider = services.BuildServiceProvider();
        var store = provider.GetService<IObservationStateStore>();

        Assert.IsNotNull(store);
        Assert.IsInstanceOfType<FileObservationStateStore>(store);
    }

    [TestMethod]
    public void AddRockBotObservation_TwiceIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotObservation();
        services.AddRockBotObservation();

        var provider = services.BuildServiceProvider();
        var stores = provider.GetServices<IObservationStateStore>().ToList();

        Assert.AreEqual(1, stores.Count, "TryAdd should keep registration idempotent");
    }

    [TestMethod]
    public void AddObservationTarget_RegistersTargetForResolution()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotObservation();

        var target = new ObservationTarget
        {
            Name = "theory-of-self",
            Filter = new EverythingFilter(),
            ExtractionPrompt = "extract",
            EvaluationPrompt = "evaluate",
            StateFilePath = "/tmp/test.json",
            OutputMarkdownPath = "/tmp/test.md",
            IncludeBehaviorSummary = true,
        };
        services.AddObservationTarget(target);

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetServices<ObservationTarget>().ToList();

        Assert.AreEqual(1, resolved.Count);
        Assert.AreEqual("theory-of-self", resolved[0].Name);
        Assert.IsTrue(resolved[0].IncludeBehaviorSummary);
    }

    [TestMethod]
    public void AddObservationTarget_MultipleTargets_AllResolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotObservation();

        services.AddObservationTarget(new ObservationTarget
        {
            Name = "theory-of-self",
            Filter = new EverythingFilter(),
            ExtractionPrompt = "x", EvaluationPrompt = "x",
            StateFilePath = "/tmp/a.json", OutputMarkdownPath = "/tmp/a.md",
        });
        services.AddObservationTarget(new ObservationTarget
        {
            Name = "theory-of-user",
            Filter = new EverythingFilter(),
            ExtractionPrompt = "x", EvaluationPrompt = "x",
            StateFilePath = "/tmp/b.json", OutputMarkdownPath = "/tmp/b.md",
        });

        var provider = services.BuildServiceProvider();
        var names = provider.GetServices<ObservationTarget>()
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "theory-of-self", "theory-of-user" }, names);
    }

    private sealed class EverythingFilter : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }
}
