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

    // ── #686: the batch's outcome and its nested calls ──────────────────────

    // The 2026-10-10 batch: seven create-then-verify wisps. Only the first create succeeded; the
    // other six hit Graph HTTP 400 and aborted at "create".
    private static ToolInvokeRequest SevenPrepBlocks() => new()
    {
        ToolCallId = "tc-686",
        ToolName = "spawn_wisps",
        Arguments = JsonSerializer.Serialize(new
        {
            definitions = Enumerable.Range(1, 7).Select(i => new
            {
                description = $"Prep block {i}",
                steps = new object[]
                {
                    new { id = "create", mode = "Direct", gateway = "Mcp", server = "calendar-mcp", tool = "create_event",
                          @params = new { title = $"Prep {i}", calendarId = "primary" } },
                    new { id = "verify", mode = "Direct", gateway = "Mcp", server = "calendar-mcp", tool = "get_event",
                          @params = new { title = $"Prep {i}" } },
                },
            }),
        }),
    };

    private static SpawnWispsExecutor CreateCalendarExecutor()
    {
        var executor = CreateSpawnExecutor(out var registry, out _);
        registry.Register(
            new ToolRegistration { Name = "mcp_invoke_tool", Description = "Invoke an MCP tool", Source = "mcp" },
            new CalendarExecutor());
        return executor;
    }

    [TestMethod]
    public async Task ExecuteAsync_SixOfSevenWispsAbort_ReportsFailedOutcome_WithNestedCalls()
    {
        var executor = CreateCalendarExecutor();
        var outcome = new ToolCallOutcome();

        ToolInvokeResponse response;
        using (ToolCallOutcomeContext.Set(outcome))
            response = await executor.ExecuteAsync(SevenPrepBlocks(), CancellationToken.None);

        Assert.IsFalse(response.IsError, "an error result would make the model re-run the wisp that worked");
        StringAssert.StartsWith(response.Content, "PARTIAL FAILURE: 6 of 7 wisps failed.");
        StringAssert.Contains(response.Content, "7 wisp(s) completed (1 succeeded, 6 failed");
        StringAssert.Contains(response.Content, "Failed at the first step run (create); nothing in this wisp completed");

        Assert.AreEqual(false, outcome.Succeeded);
        Assert.AreEqual("6 of 7 wisps failed", outcome.Detail);

        var nested = outcome.NestedSnapshot();
        Assert.AreEqual(8, nested.Count, "7 creates + 1 verify (the aborted wisps never ran verify)");
        var creates = nested.Where(c => c.Name == "calendar-mcp__create_event").ToList();
        Assert.AreEqual(7, creates.Count, "MCP steps are named by their downstream tool, not mcp_invoke_tool");
        Assert.AreEqual(1, creates.Count(c => c.Succeeded));
        Assert.IsTrue(creates.All(c => c.Detail!.StartsWith("wisp wisp-", StringComparison.Ordinal) && c.Detail.EndsWith("step create", StringComparison.Ordinal)));
        Assert.IsTrue(nested.Single(c => c.Name == "calendar-mcp__get_event").Succeeded);
        Assert.IsTrue(creates.All(ToolSideEffects.IsSideEffecting));
    }

    [TestMethod]
    public async Task ExecuteAsync_FailedCreatesSkippedPast_StillReportFailedWrites()
    {
        // The live replay on 2026-10-11: each wisp ran create with on_failure skip_to verify. All
        // three creates failed, yet every wisp counted as succeeded, and the result showed only the
        // empty verify output.
        var executor = CreateCalendarExecutor();
        var outcome = new ToolCallOutcome();
        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc-686-skip",
            ToolName = "spawn_wisps",
            Arguments = JsonSerializer.Serialize(new
            {
                definitions = Enumerable.Range(2, 3).Select(i => new
                {
                    description = $"Prep block {i}",
                    steps = new object[]
                    {
                        new { id = "create", mode = "Direct", gateway = "Mcp", server = "calendar-mcp", tool = "create_event",
                              @params = new { title = $"Prep {i}", calendarId = "primary" },
                              on_failure = new { action = "skip_to", skip_to = "verify" } },
                        new { id = "verify", mode = "Direct", gateway = "Mcp", server = "calendar-mcp", tool = "get_event",
                              @params = new { title = $"Prep {i}" } },
                    },
                }),
            }),
        };

        ToolInvokeResponse response;
        using (ToolCallOutcomeContext.Set(outcome))
            response = await executor.ExecuteAsync(request, CancellationToken.None);

        StringAssert.Contains(response.Content, "3 wisp(s) completed (3 succeeded, 0 failed");
        StringAssert.StartsWith(response.Content, "WRITES FAILED: 3 of 3 state-changing calls failed");
        StringAssert.Contains(response.Content, "Step create FAILED (handled by on_failure, the wisp continued): Failed to create event");

        Assert.AreEqual(false, outcome.Succeeded, "a batch whose writes all failed is not a success");
        Assert.AreEqual("all 3 wisps completed, but 3 of 3 state-changing calls failed (handled by on_failure)", outcome.Detail);
    }

    [TestMethod]
    public async Task ExecuteAsync_AllWispsSucceed_ReportsSuccess_WithNestedCalls()
    {
        var executor = CreateCalendarExecutor();
        var outcome = new ToolCallOutcome();
        var request = SevenPrepBlocks();
        request = new ToolInvokeRequest
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Arguments = request.Arguments!.Replace("\"primary\"", "\"default\"", StringComparison.Ordinal),
        };

        ToolInvokeResponse response;
        using (ToolCallOutcomeContext.Set(outcome))
            response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.Content!.Contains("FAILURE", StringComparison.Ordinal));
        Assert.AreEqual(true, outcome.Succeeded);
        Assert.AreEqual("all 7 wisps succeeded", outcome.Detail);
        Assert.AreEqual(14, outcome.NestedSnapshot().Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_OutsideAToolCallLoop_StillWorks()
    {
        Assert.IsNull(ToolCallOutcomeContext.Value);
        var response = await CreateCalendarExecutor().ExecuteAsync(SevenPrepBlocks(), CancellationToken.None);
        StringAssert.Contains(response.Content, "1 succeeded, 6 failed");
    }

    [TestMethod]
    public void ReportOutcome_ManyCalls_KeepsFailedAndStateChangingFirst()
    {
        var reads = Enumerable.Range(0, 60).Select(i => new WispStepResult
        {
            StepId = $"r{i}", StepIndex = i, IsSuccess = true, Duration = TimeSpan.Zero,
            ToolCalls = [new LoopToolCall("calendar-mcp__get_event", null, true)],
        });
        var failedCreate = new WispStepResult
        {
            StepId = "create", StepIndex = 60, IsSuccess = false, Duration = TimeSpan.Zero,
            ToolCalls = [new LoopToolCall("calendar-mcp__create_event", null, false)],
        };
        var batch = new WispBatchResult
        {
            BatchId = "b",
            TotalDuration = TimeSpan.Zero,
            Results =
            [
                new WispExecutionResult
                {
                    WispId = "wisp-big", IsSuccess = false, Duration = TimeSpan.Zero,
                    Definition = new WispDefinition { Description = "big", Steps = [] },
                    StepResults = [.. reads, failedCreate],
                },
            ],
        };
        var outcome = new ToolCallOutcome();

        SpawnWispsExecutor.ReportOutcome(outcome, batch);

        var nested = outcome.NestedSnapshot();
        Assert.AreEqual(SpawnWispsExecutor.MaxReportedNestedCalls, nested.Count);
        Assert.AreEqual("calendar-mcp__create_event", nested[^1].Name, "the failed create is kept, in order");
        Assert.AreEqual("1 of 1 wisps failed", outcome.Detail);
    }

    [TestMethod]
    public void FormatBatchResult_FailedAfterAnEarlierStep_NamesTheStepsThatCompleted()
    {
        var batch = new WispBatchResult
        {
            BatchId = "b",
            TotalDuration = TimeSpan.Zero,
            Results =
            [
                new WispExecutionResult
                {
                    WispId = "wisp-1", IsSuccess = false, Duration = TimeSpan.Zero,
                    Definition = new WispDefinition { Description = "Create then verify", Steps = [] },
                    StepResults =
                    [
                        new WispStepResult { StepId = "create", StepIndex = 0, IsSuccess = true, Duration = TimeSpan.Zero },
                        new WispStepResult
                        {
                            StepId = "verify", StepIndex = 1, IsSuccess = false, Duration = TimeSpan.Zero,
                            Error = new WispStepError { Category = FailureCategory.External, Message = "not found" },
                        },
                    ],
                },
            ],
        };

        var text = SpawnWispsExecutor.FormatBatchResult(batch);

        StringAssert.StartsWith(text, "ALL 1 WISPS FAILED.");
        StringAssert.Contains(text, "Steps completed before the failure: create; failed at: verify");
    }

    private sealed class CalendarExecutor : IToolExecutor
    {
        // Graph rejects calendarId "primary" (MarimerLLC/calendar-mcp#107) — except for the one
        // create in the incident that had no calendarId; here Prep 1 stands in for it.
        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
        {
            var args = request.Arguments ?? string.Empty;
            var fails = args.Contains("create_event", StringComparison.Ordinal)
                        && args.Contains("primary", StringComparison.Ordinal)
                        && !args.Contains("Prep 1", StringComparison.Ordinal);
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = fails
                    ? "Failed to create event: Microsoft Graph returned HTTP 400 (ErrorInvalidIdMalformed): The Id is invalid."
                    : "{\"id\":\"evt-1\"}",
                IsError = fails,
            });
        }
    }

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
