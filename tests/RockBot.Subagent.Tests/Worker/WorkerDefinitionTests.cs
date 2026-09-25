using RockBot.Subagent.Worker;

namespace RockBot.Subagent.Tests.Worker;

[TestClass]
public class WorkerDefinitionTests
{
    [TestMethod]
    public void ResolveResultKey_NoOverride_DefaultsToWorkerResult()
    {
        var def = new WorkerDefinition { Description = "gather" };

        Assert.AreEqual("worker/abc123/result", def.ResolveResultKey("abc123"));
    }

    [TestMethod]
    public void ResolveResultKey_BlankOverride_DefaultsToWorkerResult()
    {
        var def = new WorkerDefinition { Description = "gather", ResultKey = "  " };

        Assert.AreEqual("worker/abc123/result", def.ResolveResultKey("abc123"));
    }

    [TestMethod]
    public void ResolveResultKey_BareKey_PlacedUnderWorkerNamespace()
    {
        // A bare key used to be saved under worker/<id>/, read raw by spawn_workers, and
        // fetched under subagent/<id>/ by the spawner — three paths, findings never found.
        var def = new WorkerDefinition { Description = "gather", ResultKey = "communications-email-sweep" };

        Assert.AreEqual("worker/abc123/communications-email-sweep", def.ResolveResultKey("abc123"));
    }

    [TestMethod]
    public void ResolveResultKey_AbsoluteKey_PassesThrough()
    {
        var def = new WorkerDefinition { Description = "gather", ResultKey = "shared/patrol/calendar-latest" };

        Assert.AreEqual("shared/patrol/calendar-latest", def.ResolveResultKey("abc123"));
    }
}
