using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class SpawnWispsExecutorTests
{
    // ── Argument parsing ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_SingleDefinition_ReturnsSuccess()
    {
        var executor = CreateSpawnExecutor(out var registry, out _);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search results"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-1",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                {
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
              ]
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "1 wisp(s) completed (1 succeeded, 0 failed");
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingDefinitions_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _, out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-3",
            ToolName = "spawn_wisps",
            Arguments = """{"not_definitions": true}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Missing required argument: definitions");
    }

    [TestMethod]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _, out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-4",
            ToolName = "spawn_wisps",
            Arguments = "not valid json"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Invalid arguments JSON");
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptyDefinitionsArray_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _, out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-5",
            ToolName = "spawn_wisps",
            Arguments = """{ "definitions": [] }"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "at least one wisp definition");
    }

    [TestMethod]
    public async Task ExecuteAsync_DefinitionWithEmptySteps_ReturnsError()
    {
        var executor = CreateSpawnExecutor(out _, out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-6",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                { "description": "Empty steps", "steps": [] }
              ]
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
        var executor = CreateSpawnExecutor(out _, out _);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-7",
            ToolName = "spawn_wisps",
            Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Missing required argument: definitions");
    }

    // ── Batch execution ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecuteAsync_MultipleDefinitions_AllSucceed()
    {
        var executor = CreateSpawnExecutor(out var registry, out var memory);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));
        registry.Register(
            new ToolRegistration { Name = "web_browse", Description = "Browse", Source = "web" },
            new FakeToolExecutor("page content"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-batch-1",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                {
                  "description": "Search wisp",
                  "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "test" } }]
                },
                {
                  "description": "Browse wisp",
                  "steps": [{ "id": "b1", "mode": "Direct", "gateway": "Web", "tool": "web_browse", "params": { "url": "http://example.com" } }]
                }
              ]
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "2 wisp(s) completed (2 succeeded, 0 failed");
        StringAssert.Contains(response.Content, "Search wisp");
        StringAssert.Contains(response.Content, "Browse wisp");
        StringAssert.Contains(response.Content, "Batch ID:");
    }

    [TestMethod]
    public async Task ExecuteAsync_PartialFailure_ReportsAllResults()
    {
        var executor = CreateSpawnExecutor(out var registry, out _);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));
        registry.Register(
            new ToolRegistration { Name = "web_browse", Description = "Browse", Source = "web" },
            new FakeToolExecutor(error: "Connection refused"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-batch-2",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                {
                  "description": "Good wisp",
                  "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "test" } }]
                },
                {
                  "description": "Bad wisp",
                  "steps": [{ "id": "b1", "mode": "Direct", "gateway": "Web", "tool": "web_browse", "params": { "url": "http://down.com" } }]
                }
              ]
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        // Batch completes even with partial failure — IsError is false
        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "2 wisp(s) completed (1 succeeded, 1 failed");
        StringAssert.Contains(response.Content, "[ok]");
        StringAssert.Contains(response.Content, "[failed]");
    }

    [TestMethod]
    public async Task ExecuteAsync_WritesBatchSummaryToWorkingMemory()
    {
        var executor = CreateSpawnExecutor(out var registry, out var memory);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-batch-3",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                {
                  "description": "Memory test",
                  "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "test" } }]
                }
              ]
            }
            """
        };

        await executor.ExecuteAsync(request, CancellationToken.None);

        // Find the batch summary in working memory via the Store dictionary
        var summaryEntry = memory.Store
            .FirstOrDefault(kv => kv.Key.StartsWith("wisp/batch-") && kv.Key.EndsWith("/summary"));
        Assert.IsNotNull(summaryEntry.Value, "Batch summary should be written to working memory");

        // Verify summary content is valid JSON with expected fields
        var summary = JsonDocument.Parse(summaryEntry.Value);
        Assert.IsTrue(summary.RootElement.TryGetProperty("batchId", out _));
        Assert.IsTrue(summary.RootElement.TryGetProperty("total", out var total));
        Assert.AreEqual(1, total.GetInt32());
        Assert.IsTrue(summary.RootElement.TryGetProperty("succeeded", out var succeeded));
        Assert.AreEqual(1, succeeded.GetInt32());
    }

    [TestMethod]
    public async Task ExecuteAsync_BatchIdInLogRecords()
    {
        var log = new FakeWispExecutionLog();
        var registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions();
        var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance);
        var executor = new SpawnWispsExecutor(wispExecutor, log, feedbackStore: null, memory, options,
            NullLogger<SpawnWispsExecutor>.Instance);

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("results"));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-batch-4",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                {
                  "description": "Log test 1",
                  "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "a" } }]
                },
                {
                  "description": "Log test 2",
                  "steps": [{ "id": "s2", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "b" } }]
                }
              ]
            }
            """
        };

        await executor.ExecuteAsync(request, CancellationToken.None);

        // Give fire-and-forget logging a moment to complete
        await Task.Delay(100);

        Assert.AreEqual(2, log.Records.Count);
        Assert.IsNotNull(log.Records[0].BatchId);
        Assert.IsNotNull(log.Records[1].BatchId);
        Assert.AreEqual(log.Records[0].BatchId, log.Records[1].BatchId);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConcurrencyGating_RespectsLimit()
    {
        var registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions { MaxConcurrentWisps = 2 };
        var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance);
        var executor = new SpawnWispsExecutor(wispExecutor, executionLog: null, feedbackStore: null,
            memory, options, NullLogger<SpawnWispsExecutor>.Instance);

        var concurrencyTracker = new ConcurrencyTracker();
        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            concurrencyTracker);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-conc",
            ToolName = "spawn_wisps",
            Arguments = """
            {
              "definitions": [
                { "description": "W1", "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "1" } }] },
                { "description": "W2", "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "2" } }] },
                { "description": "W3", "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "3" } }] },
                { "description": "W4", "steps": [{ "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": { "query": "4" } }] }
              ]
            }
            """
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "4 wisp(s) completed (4 succeeded, 0 failed");
        // With limit of 2, peak concurrency should never exceed 2
        Assert.IsTrue(concurrencyTracker.PeakConcurrency <= 2,
            $"Peak concurrency was {concurrencyTracker.PeakConcurrency}, expected <= 2");
    }

    // ── Result formatting ────────────────────────────────────────────────────

    [TestMethod]
    public void FormatBatchResult_Success_IncludesBatchDetails()
    {
        var batch = new WispBatchResult
        {
            BatchId = "batch-test-fmt",
            TotalDuration = TimeSpan.FromMilliseconds(500),
            Results =
            [
                new WispExecutionResult
                {
                    WispId = "wisp-aaa",
                    IsSuccess = true,
                    Duration = TimeSpan.FromMilliseconds(300),
                    Definition = new WispDefinition { Description = "First wisp", Steps = [] },
                    StepResults =
                    [
                        new WispStepResult
                        {
                            StepId = "step1", StepIndex = 0, IsSuccess = true,
                            Content = "output data", Duration = TimeSpan.FromMilliseconds(300)
                        }
                    ]
                },
                new WispExecutionResult
                {
                    WispId = "wisp-bbb",
                    IsSuccess = true,
                    Duration = TimeSpan.FromMilliseconds(200),
                    Definition = new WispDefinition { Description = "Second wisp", Steps = [] },
                    StepResults =
                    [
                        new WispStepResult
                        {
                            StepId = "step1", StepIndex = 0, IsSuccess = true,
                            Content = "more output", Duration = TimeSpan.FromMilliseconds(200)
                        }
                    ]
                }
            ]
        };

        var formatted = SpawnWispsExecutor.FormatBatchResult(batch);

        StringAssert.Contains(formatted, "2 wisp(s) completed (2 succeeded, 0 failed");
        StringAssert.Contains(formatted, "wisp-aaa");
        StringAssert.Contains(formatted, "wisp-bbb");
        StringAssert.Contains(formatted, "First wisp");
        StringAssert.Contains(formatted, "Second wisp");
        StringAssert.Contains(formatted, "Batch ID: `batch-test-fmt`");
    }

    [TestMethod]
    public void FormatBatchResult_PartialFailure_ShowsErrors()
    {
        var batch = new WispBatchResult
        {
            BatchId = "batch-fail-fmt",
            TotalDuration = TimeSpan.FromMilliseconds(400),
            Results =
            [
                new WispExecutionResult
                {
                    WispId = "wisp-ok",
                    IsSuccess = true,
                    Duration = TimeSpan.FromMilliseconds(200),
                    Definition = new WispDefinition { Description = "Good one", Steps = [] },
                    StepResults =
                    [
                        new WispStepResult
                        {
                            StepId = "s1", StepIndex = 0, IsSuccess = true,
                            Content = "ok", Duration = TimeSpan.FromMilliseconds(200)
                        }
                    ]
                },
                new WispExecutionResult
                {
                    WispId = "wisp-bad",
                    IsSuccess = false,
                    Duration = TimeSpan.FromMilliseconds(50),
                    Definition = new WispDefinition { Description = "Bad one", Steps = [] },
                    StepResults =
                    [
                        new WispStepResult
                        {
                            StepId = "s1", StepIndex = 0, IsSuccess = false,
                            Error = new WispStepError
                            {
                                Category = FailureCategory.External,
                                Message = "Timeout",
                                ToolName = "slow_tool"
                            },
                            Duration = TimeSpan.FromMilliseconds(50)
                        }
                    ]
                }
            ]
        };

        var formatted = SpawnWispsExecutor.FormatBatchResult(batch);

        StringAssert.Contains(formatted, "2 wisp(s) completed (1 succeeded, 1 failed");
        StringAssert.Contains(formatted, "[ok]");
        StringAssert.Contains(formatted, "[failed]");
        StringAssert.Contains(formatted, "Error (External): Timeout");
        StringAssert.Contains(formatted, "slow_tool");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SpawnWispsExecutor CreateSpawnExecutor(
        out FakeToolRegistry registry, out FakeWorkingMemory memory)
    {
        registry = new FakeToolRegistry();
        memory = new FakeWorkingMemory();
        var options = new WispOptions();
        var wispLogger = NullLogger<WispExecutor>.Instance;
        var spawnLogger = NullLogger<SpawnWispsExecutor>.Instance;
        var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options, wispLogger);
        return new SpawnWispsExecutor(wispExecutor, executionLog: null, feedbackStore: null,
            memory, options, spawnLogger);
    }
}

/// <summary>
/// Tracks peak concurrency during tool execution.
/// </summary>
internal sealed class ConcurrencyTracker : IToolExecutor
{
    private int _current;
    public int PeakConcurrency { get; private set; }

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var val = Interlocked.Increment(ref _current);
        lock (this)
        {
            if (val > PeakConcurrency) PeakConcurrency = val;
        }

        await Task.Delay(50, ct); // Simulate some work

        Interlocked.Decrement(ref _current);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = "done"
        };
    }
}

/// <summary>
/// Fake execution log that captures records for assertions.
/// </summary>
internal sealed class FakeWispExecutionLog : IWispExecutionLog
{
    public List<WispExecutionRecord> Records { get; } = [];

    public Task AppendAsync(WispExecutionRecord record, CancellationToken ct)
    {
        lock (Records) { Records.Add(record); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WispExecutionRecord>>([]);

    public Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct) =>
        Task.FromResult<WispExecutionRecord?>(null);
}
