using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

// Phase 4 tests use null for execution log and feedback store in basic scenarios.

namespace RockBot.Wisp.Tests;

[TestClass]
public class SpawnWispExecutorTests
{
    // ── Argument parsing ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_ValidDefinition_ReturnsSuccess()
    {
        var executor = CreateSpawnExecutor(out var registry);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search results"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-1",
            ToolName = "spawn_wisp",
            Arguments = """
            {
              "definition": {
                "description": "Simple search",
                "steps": [
                  {
                    "id": "search",
                    "mode": "Direct",
                    "gateway": "Web",
                    "tool": "web_search",
                    "params": { "query": "test" }
                  }
                ]
              }
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "completed successfully");
        StringAssert.Contains(response.Content, "search");
    }

    [TestMethod]
    public async Task ExecuteAsync_DefinitionAsJsonString_ParsesCorrectly()
    {
        var executor = CreateSpawnExecutor(out var registry);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        // Definition passed as a JSON string (escaped), which some LLMs may produce
        var innerDef = """{"description":"String def","steps":[{"id":"s1","mode":"Direct","gateway":"Web","tool":"web_search","params":{"query":"test"}}]}""";
        var args = JsonSerializer.Serialize(new { definition = innerDef });

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-2",
            ToolName = "spawn_wisp",
            Arguments = args
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "completed successfully");
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingDefinition_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-3",
            ToolName = "spawn_wisp",
            Arguments = """{"not_definition": true}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Missing required argument: definition");
    }

    [TestMethod]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-4",
            ToolName = "spawn_wisp",
            Arguments = "not valid json"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Invalid arguments JSON");
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptySteps_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-5",
            ToolName = "spawn_wisp",
            Arguments = """
            {
              "definition": {
                "description": "Empty steps",
                "steps": []
              }
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "at least one step");
    }

    [TestMethod]
    public async Task ExecuteAsync_NullArguments_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-6",
            ToolName = "spawn_wisp",
            Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Missing required argument: definition");
    }

    // ── Failed wisp execution ────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_WispFails_ReturnsErrorWithClassification()
    {
        var executor = CreateSpawnExecutor(out var registry);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor(error: "Service unavailable"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-7",
            ToolName = "spawn_wisp",
            Arguments = """
            {
              "definition": {
                "description": "Failing wisp",
                "steps": [
                  {
                    "id": "search",
                    "mode": "Direct",
                    "gateway": "Web",
                    "tool": "web_search",
                    "params": { "query": "test" }
                  }
                ]
              }
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "failed at step");
        StringAssert.Contains(response.Content, "Error category:");
    }

    // ── Result formatting ────────────────────────────────────────────────────

    [TestMethod]
    public void FormatResult_Success_IncludesStepDetails()
    {
        var result = new WispExecutionResult
        {
            WispId = "wisp-test-fmt",
            IsSuccess = true,
            Duration = TimeSpan.FromMilliseconds(150),
            Definition = new WispDefinition { Description = "test", Steps = [] },
            StepResults =
            [
                new WispStepResult
                {
                    StepId = "step1",
                    StepIndex = 0,
                    IsSuccess = true,
                    Content = "output data",
                    Duration = TimeSpan.FromMilliseconds(100)
                }
            ]
        };

        var formatted = SpawnWispExecutor.FormatResult(result);

        StringAssert.Contains(formatted, "completed successfully");
        StringAssert.Contains(formatted, "step1");
        StringAssert.Contains(formatted, "output data");
        StringAssert.Contains(formatted, "wisp/wisp-test-fmt");
    }

    [TestMethod]
    public void FormatResult_Failure_IncludesErrorDetails()
    {
        var result = new WispExecutionResult
        {
            WispId = "wisp-test-fail",
            IsSuccess = false,
            Duration = TimeSpan.FromMilliseconds(50),
            Definition = new WispDefinition { Description = "test", Steps = [] },
            StepResults =
            [
                new WispStepResult
                {
                    StepId = "bad_step",
                    StepIndex = 0,
                    IsSuccess = false,
                    Error = new WispStepError
                    {
                        Category = FailureCategory.Structural,
                        Message = "Tool not found",
                        ToolName = "missing_tool"
                    },
                    Duration = TimeSpan.FromMilliseconds(5)
                }
            ]
        };

        var formatted = SpawnWispExecutor.FormatResult(result);

        StringAssert.Contains(formatted, "failed at step `bad_step`");
        StringAssert.Contains(formatted, "Structural");
        StringAssert.Contains(formatted, "Tool not found");
        StringAssert.Contains(formatted, "missing_tool");
        StringAssert.Contains(formatted, "preserved for debugging");
    }

    [TestMethod]
    public void FormatResult_LongOutput_Truncated()
    {
        var longContent = new string('x', 1000);
        var result = new WispExecutionResult
        {
            WispId = "wisp-trunc",
            IsSuccess = true,
            Duration = TimeSpan.FromMilliseconds(10),
            Definition = new WispDefinition { Description = "test", Steps = [] },
            StepResults =
            [
                new WispStepResult
                {
                    StepId = "s1",
                    StepIndex = 0,
                    IsSuccess = true,
                    Content = longContent,
                    Duration = TimeSpan.FromMilliseconds(5)
                }
            ]
        };

        var formatted = SpawnWispExecutor.FormatResult(result);

        StringAssert.Contains(formatted, "chars total");
        Assert.IsTrue(formatted.Length < longContent.Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SpawnWispExecutor CreateSpawnExecutor(out FakeToolRegistry registry)
    {
        registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions();
        var wispLogger = NullLogger<WispExecutor>.Instance;
        var spawnLogger = NullLogger<SpawnWispExecutor>.Instance;
        var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options, wispLogger);
        return new SpawnWispExecutor(wispExecutor, executionLog: null, feedbackStore: null, spawnLogger);
    }
}
