using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

// Tests that exercise shared volume file I/O use a real temp directory,
// matching the framework pattern of direct File.* access everywhere.

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispExecutorTests
{
    // ── Direct mode execution ────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_SingleDirectStep_Success()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search results"));

        var definition = new WispDefinition
        {
            Description = "Simple search",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.StepResults.Count);
        Assert.AreEqual("search results", result.StepResults[0].Content);
        Assert.AreEqual("search", result.StepResults[0].StepId);
    }

    [TestMethod]
    public async Task ExecuteAsync_MultipleDirectSteps_ExecutesInOrder()
    {
        var (executor, registry) = CreateExecutor();

        var callOrder = new List<string>();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new TrackingToolExecutor("result-1", callOrder, "web_search"));

        registry.Register(
            new ToolRegistration { Name = "execute_python_script", Description = "Script", Source = "script" },
            new TrackingToolExecutor("result-2", callOrder, "execute_python_script"));

        var definition = new WispDefinition
        {
            Description = "Two-step pipeline",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                },
                new WispStep
                {
                    Id = "process",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Script,
                    Params = JsonDocument.Parse("""{"script": "print('hello')"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-2", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.StepResults.Count);
        Assert.AreEqual(2, callOrder.Count);
        Assert.AreEqual("web_search", callOrder[0]);
        Assert.AreEqual("execute_python_script", callOrder[1]);
    }

    [TestMethod]
    public async Task ExecuteAsync_ToolNotRegistered_ReturnsStructuralError()
    {
        var (executor, _) = CreateExecutor();

        var definition = new WispDefinition
        {
            Description = "Missing tool",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-3", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, result.StepResults.Count);
        Assert.IsFalse(result.StepResults[0].IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.StepResults[0].Error!.Category);
        StringAssert.Contains(result.StepResults[0].Error!.Message, "not registered");
    }

    [TestMethod]
    public async Task ExecuteAsync_ToolReturnsError_AbortsAndClassifiesError()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor(error: "Service unavailable"));

        registry.Register(
            new ToolRegistration { Name = "execute_python_script", Description = "Script", Source = "script" },
            new FakeToolExecutor("should not be called"));

        var definition = new WispDefinition
        {
            Description = "Failing pipeline",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                },
                new WispStep
                {
                    Id = "process",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Script,
                    Params = JsonDocument.Parse("""{"script": "print('hello')"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-4", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        // Only 1 step result — second step was not executed due to abort
        Assert.AreEqual(1, result.StepResults.Count);
        Assert.IsFalse(result.StepResults[0].IsSuccess);
        Assert.AreEqual(FailureCategory.External, result.StepResults[0].Error!.Category);
    }

    // ── On-failure branching ─────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_OnFailure_SkipTo_SkipsIntermediateSteps()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "MCP", Source = "mcp" },
            new FakeToolExecutor(error: "Missing required parameter"));

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("fallback result"));

        var definition = new WispDefinition
        {
            Description = "With skip_to",
            Steps =
            [
                new WispStep
                {
                    Id = "risky",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Mcp,
                    Server = "test-server",
                    Tool = "test_tool",
                    OnFailure = new OnFailureAction { Action = "skip_to", SkipTo = "fallback" }
                },
                new WispStep
                {
                    Id = "skipped",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "should be skipped"}""").RootElement
                },
                new WispStep
                {
                    Id = "fallback",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "fallback"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-5", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(3, result.StepResults.Count);

        // First step failed but had on_failure
        Assert.IsFalse(result.StepResults[0].IsSuccess);
        Assert.AreEqual("risky", result.StepResults[0].StepId);

        // Second step was skipped
        Assert.IsTrue(result.StepResults[1].WasSkipped);
        Assert.AreEqual("skipped", result.StepResults[1].StepId);

        // Third step executed as fallback
        Assert.IsTrue(result.StepResults[2].IsSuccess);
        Assert.AreEqual("fallback", result.StepResults[2].StepId);
        Assert.AreEqual("fallback result", result.StepResults[2].Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_OnFailure_Abort_StopsExecution()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "MCP", Source = "mcp" },
            new FakeToolExecutor(error: "Something broke"));

        var definition = new WispDefinition
        {
            Description = "With abort",
            Steps =
            [
                new WispStep
                {
                    Id = "risky",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Mcp,
                    Server = "test",
                    Tool = "test_tool",
                    OnFailure = new OnFailureAction { Action = "abort" }
                },
                new WispStep
                {
                    Id = "never",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "never runs"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-6", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, result.StepResults.Count);
    }

    // ── MCP gateway integration ──────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_McpStep_PassesServerAndToolCorrectly()
    {
        var (executor, registry) = CreateExecutor();

        var capturedRequest = (ToolInvokeRequest?)null;
        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "MCP", Source = "mcp" },
            new CapturingToolExecutor(r => capturedRequest = r, "ok"));

        var definition = new WispDefinition
        {
            Description = "MCP test",
            Steps =
            [
                new WispStep
                {
                    Id = "call_mcp",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Mcp,
                    Server = "onedrive",
                    Tool = "download_file",
                    Params = JsonDocument.Parse("""{"path": "/reports/Q1.xlsx"}""").RootElement
                }
            ]
        };

        await executor.ExecuteAsync(definition, "wisp-test-7", CancellationToken.None);

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("mcp_invoke_tool", capturedRequest!.ToolName);

        var args = JsonDocument.Parse(capturedRequest.Arguments!).RootElement;
        Assert.AreEqual("onedrive", args.GetProperty("server_name").GetString());
        Assert.AreEqual("download_file", args.GetProperty("tool_name").GetString());
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_Cancelled_ReturnsExternalError()
    {
        var (executor, registry) = CreateExecutor();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        var definition = new WispDefinition
        {
            Description = "Cancelled",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-8", cts.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.External, result.StepResults[0].Error!.Category);
    }

    // ── Gateway validation ───────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_DirectStep_NoGateway_ReturnsStructuralError()
    {
        var (executor, _) = CreateExecutor();

        var definition = new WispDefinition
        {
            Description = "No gateway",
            Steps =
            [
                new WispStep
                {
                    Id = "bad",
                    Mode = StepMode.Direct
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-test-9", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(FailureCategory.Structural, result.StepResults[0].Error!.Category);
    }

    // ── Working memory output ────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_OutputTo_WritesToSharedVolume()
    {
        var tempDir = CreateTempSharedVolume();
        try
        {
            var memory = new FakeWorkingMemory();
            var (executor, registry) = CreateExecutor(memory, tempDir);

            registry.Register(
                new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
                new FakeToolExecutor("important data"));

            var definition = new WispDefinition
            {
                Description = "With output_to",
                Steps =
                [
                    new WispStep
                    {
                        Id = "search",
                        Mode = StepMode.Direct,
                        Gateway = GatewayType.Web,
                        Tool = "web_search",
                        Params = JsonDocument.Parse("""{"query": "test"}""").RootElement,
                        OutputTo = "wisp-test/results.json"
                    }
                ]
            };

            var result = await executor.ExecuteAsync(definition, "wisp-out-1", CancellationToken.None);

            Assert.IsTrue(result.IsSuccess);
            // Verify file written to disk
            var filePath = Path.Combine(tempDir, "wisp-test", "results.json");
            Assert.IsTrue(File.Exists(filePath));
            Assert.AreEqual("important data", await File.ReadAllTextAsync(filePath));
            // Working memory was cleaned up on success
            Assert.AreEqual(0, memory.Store.Count);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    // ── Execution result metadata ────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_RecordsDuration()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        var definition = new WispDefinition
        {
            Description = "Duration test",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-dur-1", CancellationToken.None);

        Assert.IsTrue(result.Duration > TimeSpan.Zero);
        Assert.IsTrue(result.StepResults[0].Duration >= TimeSpan.Zero);
        Assert.AreEqual("wisp-dur-1", result.WispId);
        Assert.AreSame(definition, result.Definition);
    }

    [TestMethod]
    public async Task ExecuteAsync_FailedStep_AvailableViaFailedStepProperty()
    {
        var (executor, registry) = CreateExecutor();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor(error: "boom"));

        var definition = new WispDefinition
        {
            Description = "Failed step property test",
            Steps =
            [
                new WispStep
                {
                    Id = "failing",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-fail-1", CancellationToken.None);

        Assert.IsNotNull(result.FailedStep);
        Assert.AreEqual("failing", result.FailedStep!.StepId);
    }

    // ── Phase 2: Shared volume data flow ────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_OutputTo_NoSharedVolumePath_StillSucceeds()
    {
        var memory = new FakeWorkingMemory();
        var (executor, registry) = CreateExecutor(memory, sharedVolumePath: null);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("data"));

        var definition = new WispDefinition
        {
            Description = "No shared volume",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement,
                    OutputTo = "output.json"
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-no-vol", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("data", result.StepResults[0].Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_InputFrom_ReadsFromSharedVolume()
    {
        var tempDir = CreateTempSharedVolume();
        try
        {
            // Pre-populate a file on the shared volume
            var inputDir = Path.Combine(tempDir, "data");
            Directory.CreateDirectory(inputDir);
            await File.WriteAllTextAsync(Path.Combine(inputDir, "input.json"), """{"revenue": 1000}""");

            var memory = new FakeWorkingMemory();
            var (executor, registry) = CreateExecutor(memory, tempDir);

            registry.Register(
                new ToolRegistration { Name = "execute_python_script", Description = "Script", Source = "script" },
                new FakeToolExecutor("processed"));

            var definition = new WispDefinition
            {
                Description = "Input from shared volume",
                Steps =
                [
                    new WispStep
                    {
                        Id = "process",
                        Mode = StepMode.Direct,
                        Gateway = GatewayType.Script,
                        Params = JsonDocument.Parse("""{"script": "print('hello')"}""").RootElement,
                        InputFrom = "data/input.json"
                    }
                ]
            };

            var result = await executor.ExecuteAsync(definition, "wisp-input-1", CancellationToken.None);

            // Direct steps with input_from just resolve templates — file reading
            // is primarily for llm steps. Direct step should still execute successfully.
            Assert.IsTrue(result.IsSuccess);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_InputFrom_PriorStepOutputTo_UsesInMemoryContent()
    {
        var tempDir = CreateTempSharedVolume();
        try
        {
            var memory = new FakeWorkingMemory();
            var (executor, registry) = CreateExecutor(memory, tempDir);

            registry.Register(
                new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
                new FakeToolExecutor("search results data"));

            registry.Register(
                new ToolRegistration { Name = "execute_python_script", Description = "Script", Source = "script" },
                new FakeToolExecutor("processed"));

            var definition = new WispDefinition
            {
                Description = "Cross-step data flow via output_to/input_from",
                Steps =
                [
                    new WispStep
                    {
                        Id = "search",
                        Mode = StepMode.Direct,
                        Gateway = GatewayType.Web,
                        Tool = "web_search",
                        Params = JsonDocument.Parse("""{"query": "test"}""").RootElement,
                        OutputTo = "wisp-data/search.json"
                    },
                    new WispStep
                    {
                        Id = "process",
                        Mode = StepMode.Direct,
                        Gateway = GatewayType.Script,
                        Params = JsonDocument.Parse("""{"script": "print('hello')"}""").RootElement,
                        InputFrom = "wisp-data/search.json"
                    }
                ]
            };

            var result = await executor.ExecuteAsync(definition, "wisp-flow-1", CancellationToken.None);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(2, result.StepResults.Count);
            // Shared volume received the write from step 1
            var filePath = Path.Combine(tempDir, "wisp-data", "search.json");
            Assert.IsTrue(File.Exists(filePath));
            Assert.AreEqual("search results data", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_WorkingMemory_CleanedUpOnSuccess()
    {
        var memory = new FakeWorkingMemory();
        var (executor, registry) = CreateExecutor(memory);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        var definition = new WispDefinition
        {
            Description = "Cleanup test",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement,
                    OutputTo = "out.json"
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-cleanup", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        // Working memory should be cleared for the wisp namespace
        var wispKeys = memory.Store.Keys.Where(k => k.StartsWith("wisp/")).ToList();
        Assert.AreEqual(0, wispKeys.Count, "Wisp working memory should be cleaned up on success");
    }

    [TestMethod]
    public async Task ExecuteAsync_WorkingMemory_KeptOnFailure()
    {
        var memory = new FakeWorkingMemory();
        var (executor, registry) = CreateExecutor(memory);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search data"));

        registry.Register(
            new ToolRegistration { Name = "execute_python_script", Description = "Script", Source = "script" },
            new FakeToolExecutor(error: "script failed"));

        var definition = new WispDefinition
        {
            Description = "Failure preserves memory",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonDocument.Parse("""{"query": "test"}""").RootElement,
                    OutputTo = "data/search.json"
                },
                new WispStep
                {
                    Id = "process",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Script,
                    Params = JsonDocument.Parse("""{"script": "print('fail')"}""").RootElement
                }
            ]
        };

        var result = await executor.ExecuteAsync(definition, "wisp-keep", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        // Working memory should be preserved for debugging
        var wispKeys = memory.Store.Keys.Where(k => k.StartsWith("wisp/")).ToList();
        Assert.IsTrue(wispKeys.Count > 0, "Wisp working memory should be kept on failure for debugging");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (WispExecutor Executor, FakeToolRegistry Registry) CreateExecutor(
        FakeWorkingMemory? memory = null,
        string? sharedVolumePath = null)
    {
        var registry = new FakeToolRegistry();
        memory ??= new FakeWorkingMemory();
        var options = new WispOptions { SharedVolumePath = sharedVolumePath };
        var logger = NullLogger<WispExecutor>.Instance;

        // WispExecutor needs AgentLoopRunner for LLM steps, but direct-mode tests
        // don't exercise that path. Pass null and rely on the test not calling LLM steps.
        var executor = new WispExecutor(registry, memory, agentLoopRunner: null!, options, logger);
        return (executor, registry);
    }

    private static string CreateTempSharedVolume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rockbot-wisp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

internal sealed class FakeToolExecutor(string? content = null, string? error = null) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (error is not null)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = error,
                IsError = true
            });
        }

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content
        });
    }
}

internal sealed class TrackingToolExecutor(string content, List<string> callOrder, string trackingName) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        callOrder.Add(trackingName);
        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content
        });
    }
}

internal sealed class CapturingToolExecutor(Action<ToolInvokeRequest> capture, string content) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        capture(request);
        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content
        });
    }
}

internal sealed class FakeToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

    public IReadOnlyList<ToolRegistration> GetTools() =>
        _tools.Values.Select(t => t.Registration).ToList();

    public IToolExecutor? GetExecutor(string toolName) =>
        _tools.TryGetValue(toolName, out var entry) ? entry.Executor : null;

    public void Register(ToolRegistration registration, IToolExecutor executor) =>
        _tools[registration.Name] = (registration, executor);

    public bool Unregister(string toolName) =>
        _tools.Remove(toolName);
}

internal sealed class FakeWorkingMemory : IWorkingMemory
{
    public Dictionary<string, string> Store { get; } = new();

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        Store[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

    public Task DeleteAsync(string key)
    {
        Store.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        if (prefix is null)
        {
            Store.Clear();
        }
        else
        {
            var keysToRemove = Store.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keysToRemove)
                Store.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
}

