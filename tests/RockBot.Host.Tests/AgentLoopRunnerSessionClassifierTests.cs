using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for the session-kind classifier used as a tag on the per-LLM-call context-size
/// histogram. The buckets match the gate on <see cref="AgentLoopRunner"/>'s
/// ContextBreakdown logging so the metric and the log share a vocabulary.
/// </summary>
[TestClass]
public class AgentLoopRunnerSessionClassifierTests
{
    [TestMethod]
    public void Classify_PatrolPrefix_ReturnsPatrol()
    {
        Assert.AreEqual("patrol", AgentLoopRunner.ClassifySessionKind("patrol/heartbeat-patrol"));
        Assert.AreEqual("patrol", AgentLoopRunner.ClassifySessionKind("patrol/mailbox-triage"));
    }

    [TestMethod]
    public void Classify_SubagentPrefix_ReturnsSubagent()
    {
        Assert.AreEqual("subagent", AgentLoopRunner.ClassifySessionKind("subagent-fa2900996765"));
        Assert.AreEqual("subagent", AgentLoopRunner.ClassifySessionKind("subagent-abc123"));
    }

    [TestMethod]
    public void Classify_WorkerPrefix_ReturnsWorker()
    {
        Assert.AreEqual("worker", AgentLoopRunner.ClassifySessionKind("worker-bab999deb274"));
    }

    [TestMethod]
    public void Classify_PlainSessionId_ReturnsSession()
    {
        Assert.AreEqual("session", AgentLoopRunner.ClassifySessionKind("session/9bcce906b50f"));
        Assert.AreEqual("session", AgentLoopRunner.ClassifySessionKind("just-a-random-id"));
        Assert.AreEqual("session", AgentLoopRunner.ClassifySessionKind(""));
    }

    [TestMethod]
    public void Classify_Null_ReturnsUnknown()
    {
        Assert.AreEqual("unknown", AgentLoopRunner.ClassifySessionKind(null));
    }

    [TestMethod]
    public void Classify_CaseSensitive_DoesNotMatchUppercase()
    {
        // The session-id schema is fixed lowercase in code; an uppercase prefix would be
        // a bug elsewhere, not something the classifier should silently normalise.
        Assert.AreEqual("session", AgentLoopRunner.ClassifySessionKind("PATROL/x"));
        Assert.AreEqual("session", AgentLoopRunner.ClassifySessionKind("Subagent-x"));
    }
}
