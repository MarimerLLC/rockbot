using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Agent.A2A.Tests;

[TestClass]
public class InboundA2AToolSetTests
{
    [TestMethod]
    public void Build_IncludesWorkingMemoryTools()
    {
        var wm = new StubWorkingMemory();
        var memoryTools = CreateMemoryTools();

        var tools = InboundA2AToolSet.Build(wm, memoryTools, "task-123", NullLogger.Instance);

        // WorkingMemoryTools creates 5 tools (save, get, list, delete, search)
        Assert.IsTrue(tools.Count >= 5, $"Expected at least 5 tools but got {tools.Count}");
    }

    [TestMethod]
    public void Build_IncludesSearchMemoryFromLongTermMemory()
    {
        var wm = new StubWorkingMemory();
        var memoryTools = CreateMemoryTools();

        var tools = InboundA2AToolSet.Build(wm, memoryTools, "task-123", NullLogger.Instance);

        var hasSearchMemory = tools.OfType<AIFunction>().Any(f => f.Name == "SearchMemory");
        Assert.IsTrue(hasSearchMemory, "Expected SearchMemory tool from long-term memory tools");
    }

    [TestMethod]
    public void Build_DoesNotIncludeSaveMemory()
    {
        var wm = new StubWorkingMemory();
        var memoryTools = CreateMemoryTools();

        var tools = InboundA2AToolSet.Build(wm, memoryTools, "task-123", NullLogger.Instance);

        // Should only include SearchMemory from long-term memory, not SaveMemory
        var ltmTools = memoryTools.Tools.OfType<AIFunction>()
            .Where(f => f.Name != "SearchMemory")
            .Select(f => f.Name)
            .ToList();

        foreach (var ltmToolName in ltmTools)
        {
            var found = tools.OfType<AIFunction>().Any(f => f.Name == ltmToolName);
            Assert.IsFalse(found, $"Long-term memory tool '{ltmToolName}' should not be in the restricted tool set");
        }
    }

    [TestMethod]
    public void Build_ScopesWorkingMemoryToTaskNamespace()
    {
        var wm = new StubWorkingMemory();
        var memoryTools = CreateMemoryTools();

        var tools1 = InboundA2AToolSet.Build(wm, memoryTools, "task-aaa", NullLogger.Instance);
        var tools2 = InboundA2AToolSet.Build(wm, memoryTools, "task-bbb", NullLogger.Instance);

        // Different task IDs should produce different tool instances
        // (they have different namespaces: a2a-inbox/task-aaa vs a2a-inbox/task-bbb)
        Assert.AreNotEqual(tools1.Count, 0);
        Assert.AreNotEqual(tools2.Count, 0);
    }

    private static MemoryTools CreateMemoryTools()
    {
        var profileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), $"rockbot-test-{Guid.NewGuid():N}")
        });

        return new MemoryTools(
            new StubLongTermMemory(),
            new StubLlmClient(),
            profileOptions,
            NullLogger<MemoryTools>.Instance);
    }
}
