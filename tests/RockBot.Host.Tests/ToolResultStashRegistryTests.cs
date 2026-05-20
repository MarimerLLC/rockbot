namespace RockBot.Host.Tests;

[TestClass]
public class ToolResultStashRegistryTests
{
    [TestMethod]
    public void IsEmpty_WhenNew_ReturnsTrue()
    {
        var registry = new ToolResultStashRegistry();
        Assert.IsTrue(registry.IsEmpty);
        Assert.AreEqual(0, registry.Snapshot().Count);
    }

    [TestMethod]
    public void Add_ThenContains_ReturnsTrue()
    {
        var registry = new ToolResultStashRegistry();
        var entry = new ToolResultStashRegistry.Entry(
            CallId: "call-1",
            ToolName: "mcp_invoke_tool",
            ArgsSummary: "server=foo, tool=bar",
            Key: "stash/sess/call-1");

        registry.Add(entry);

        Assert.IsTrue(registry.Contains("call-1"));
        Assert.IsFalse(registry.IsEmpty);
    }

    [TestMethod]
    public void Add_Snapshot_ReturnsAddedEntries()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry("c1", "tool_a", "x=1", "stash/_/c1"));
        registry.Add(new ToolResultStashRegistry.Entry("c2", "tool_b", "y=2", "stash/_/c2"));

        var snapshot = registry.Snapshot();

        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual("c1", snapshot[0].CallId);
        Assert.AreEqual("c2", snapshot[1].CallId);
    }

    [TestMethod]
    public void Add_DuplicateCallId_IsIgnored()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry("c1", "tool_a", "x=1", "stash/_/c1"));
        registry.Add(new ToolResultStashRegistry.Entry("c1", "tool_a", "x=2", "stash/_/c1-other"));

        var snapshot = registry.Snapshot();

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual("x=1", snapshot[0].ArgsSummary,
            "First add wins — duplicate call ids must not overwrite the original entry.");
    }

    [TestMethod]
    public void Add_EmptyCallId_IsIgnored()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry("", "tool_a", "x=1", "stash/_/"));

        Assert.IsTrue(registry.IsEmpty);
        Assert.IsFalse(registry.Contains(""));
    }

    [TestMethod]
    public void Contains_UnknownCallId_ReturnsFalse()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry("c1", "tool_a", "x=1", "stash/_/c1"));

        Assert.IsFalse(registry.Contains("c2"));
        Assert.IsFalse(registry.Contains(""));
    }

    [TestMethod]
    public async Task Add_ConcurrentFromMultipleThreads_IsThreadSafe()
    {
        var registry = new ToolResultStashRegistry();
        var tasks = new List<Task>();
        const int writers = 16;
        const int perWriter = 50;

        for (var w = 0; w < writers; w++)
        {
            var writerId = w;
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    var callId = $"w{writerId}-c{i}";
                    registry.Add(new ToolResultStashRegistry.Entry(
                        callId, "tool", "args", $"stash/_/{callId}"));
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.AreEqual(writers * perWriter, registry.Snapshot().Count);
    }
}
