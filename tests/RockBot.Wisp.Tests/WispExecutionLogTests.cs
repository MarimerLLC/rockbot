using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispExecutionLogTests
{
    // ── FileWispExecutionLog ─────────────────────────────────────────────────

    [TestMethod]
    public async Task AppendAsync_WritesAndQueryReturnsRecord()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            var record = new WispExecutionRecord
            {
                WispId = "wisp-test-1",
                Description = "Test wisp",
                DefinitionHash = "abc123",
                Succeeded = true,
                StepCount = 2,
                StepsCompleted = 2,
                DurationMs = 100,
                Timestamp = DateTimeOffset.UtcNow
            };

            await log.AppendAsync(record);

            var results = await log.QueryRecentAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("wisp-test-1", results[0].WispId);
            Assert.IsTrue(results[0].Succeeded);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task AppendAsync_FailedRecord_PreservesFailureDetails()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            var record = new WispExecutionRecord
            {
                WispId = "wisp-fail-1",
                Description = "Failing wisp",
                DefinitionHash = "def456",
                Succeeded = false,
                StepCount = 3,
                StepsCompleted = 1,
                FailedStepId = "step2",
                FailedStepIndex = 1,
                FailureCategory = "Structural",
                ErrorMessage = "Tool not found",
                FailedToolName = "missing_tool",
                DurationMs = 50,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = "session-abc"
            };

            await log.AppendAsync(record);

            var results = await log.QueryRecentAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.AreEqual("step2", results[0].FailedStepId);
            Assert.AreEqual("Structural", results[0].FailureCategory);
            Assert.AreEqual("Tool not found", results[0].ErrorMessage);
            Assert.AreEqual("missing_tool", results[0].FailedToolName);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindRecentFailureAsync_MatchesDefinitionHash()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            // Add a failed record
            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-prior-fail",
                Description = "Search and process",
                DefinitionHash = "hash-abc",
                Succeeded = false,
                StepCount = 2,
                StepsCompleted = 0,
                FailedStepId = "search",
                FailureCategory = "Structural",
                ErrorMessage = "Bad tool name",
                DurationMs = 10,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = "session-1"
            });

            // Query for a matching failure
            var found = await log.FindRecentFailureAsync("hash-abc", "session-1");

            Assert.IsNotNull(found);
            Assert.AreEqual("wisp-prior-fail", found!.WispId);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task FindRecentFailureAsync_NoMatch_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            // Add a successful record (not a failure)
            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-ok",
                Description = "Test",
                DefinitionHash = "hash-xyz",
                Succeeded = true,
                StepCount = 1,
                StepsCompleted = 1,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow
            });

            var found = await log.FindRecentFailureAsync("hash-xyz", null);

            Assert.IsNull(found);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task QueryRecentAsync_FiltersOldRecords()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-old",
                Description = "Old",
                DefinitionHash = "h1",
                Succeeded = true,
                StepCount = 1,
                StepsCompleted = 1,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-30)
            });

            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-recent",
                Description = "Recent",
                DefinitionHash = "h2",
                Succeeded = true,
                StepCount = 1,
                StepsCompleted = 1,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow
            });

            var results = await log.QueryRecentAsync(DateTimeOffset.UtcNow.AddDays(-7), 100);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("wisp-recent", results[0].WispId);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    // ── WispExecutionRecord serialization ────────────────────────────────────

    [TestMethod]
    public void WispExecutionRecord_RoundTripsViaJson()
    {
        var record = new WispExecutionRecord
        {
            WispId = "wisp-rt",
            Description = "Round-trip test",
            DefinitionHash = "hash123",
            Succeeded = false,
            StepCount = 3,
            StepsCompleted = 1,
            FailedStepId = "step2",
            FailedStepIndex = 1,
            FailureCategory = "Data",
            ErrorMessage = "Parse error",
            FailedToolName = "execute_python_script",
            DurationMs = 250,
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = "sess-abc",
            RetryOf = "wisp-prior"
        };

        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var deserialized = JsonSerializer.Deserialize<WispExecutionRecord>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        });

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("wisp-rt", deserialized!.WispId);
        Assert.AreEqual("Data", deserialized.FailureCategory);
        Assert.AreEqual("wisp-prior", deserialized.RetryOf);
        Assert.AreEqual("step2", deserialized.FailedStepId);
    }

    // ── Correction pair detection ────────────────────────────────────────────

    [TestMethod]
    public async Task SpawnWispsExecutor_SuccessfulRetry_LogsWithRetryOf()
    {
        var tempDir = CreateTempDir();
        try
        {
            var executionLog = CreateLog(tempDir);
            var feedbackStore = new FakeFeedbackStore();
            var registry = new FakeToolRegistry();
            var memory = new FakeWorkingMemory();
            var options = new WispOptions();
            var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
                NullLogger<WispExecutor>.Instance);
            var spawnExecutor = new SpawnWispsExecutor(wispExecutor, executionLog, feedbackStore,
                memory, options, NullLogger<SpawnWispsExecutor>.Instance);

            // Register a tool that fails first, then succeeds
            var callCount = 0;
            registry.Register(
                new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
                new ConditionalToolExecutor(() =>
                {
                    callCount++;
                    return callCount <= 1 ? ("Service unavailable", true) : ("results", false);
                }));

            var defJson = """
            {
              "definitions": [
                {
                  "description": "Search task",
                  "steps": [{"id":"search","mode":"Direct","gateway":"Web","tool":"web_search","params":{"query":"test"}}]
                }
              ]
            }
            """;

            // First call — should fail (batch still returns IsError=false, check content)
            var req1 = new ToolInvokeRequest { ToolCallId = "tc-1", ToolName = "spawn_wisps", Arguments = defJson, SessionId = "sess-1" };
            var resp1 = await spawnExecutor.ExecuteAsync(req1, CancellationToken.None);
            Assert.IsFalse(resp1.IsError);
            StringAssert.Contains(resp1.Content, "0 succeeded, 1 failed");

            // Wait for async logging
            await Task.Delay(100);

            // Second call — should succeed and detect as retry
            var req2 = new ToolInvokeRequest { ToolCallId = "tc-2", ToolName = "spawn_wisps", Arguments = defJson, SessionId = "sess-1" };
            var resp2 = await spawnExecutor.ExecuteAsync(req2, CancellationToken.None);
            Assert.IsFalse(resp2.IsError);
            StringAssert.Contains(resp2.Content, "1 succeeded, 0 failed");

            // Wait for async logging
            await Task.Delay(100);

            // Verify execution log has both records
            var records = await executionLog.QueryRecentAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.AreEqual(2, records.Count);

            // Second record should reference the first as RetryOf
            var successRecord = records.First(r => r.Succeeded);
            Assert.IsNotNull(successRecord.RetryOf);

            // Feedback store should have a WispCorrection entry
            Assert.AreEqual(1, feedbackStore.Entries.Count);
            Assert.AreEqual(FeedbackSignalType.WispCorrection, feedbackStore.Entries[0].SignalType);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    // ── Definition hash ──────────────────────────────────────────────────────

    [TestMethod]
    public void ComputeDefinitionHash_SameInput_SameHash()
    {
        var json = """{"description":"test","steps":[]}""";
        var hash1 = SpawnWispsExecutor.ComputeDefinitionHash(json);
        var hash2 = SpawnWispsExecutor.ComputeDefinitionHash(json);

        Assert.AreEqual(hash1, hash2);
        Assert.AreEqual(16, hash1.Length);
    }

    [TestMethod]
    public void ComputeDefinitionHash_DifferentInput_DifferentHash()
    {
        var hash1 = SpawnWispsExecutor.ComputeDefinitionHash("""{"a":1}""");
        var hash2 = SpawnWispsExecutor.ComputeDefinitionHash("""{"a":2}""");

        Assert.AreNotEqual(hash1, hash2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static FileWispExecutionLog CreateLog(string basePath)
    {
        var options = new WispOptions { SharedVolumePath = basePath };
        return new FileWispExecutionLog(options, NullLogger<FileWispExecutionLog>.Instance);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rockbot-wisp-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}

// ── Additional test doubles for Phase 4 ──────────────────────────────────────

internal sealed class FakeFeedbackStore : IFeedbackStore
{
    public List<FeedbackEntry> Entries { get; } = [];

    public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>(Entries.Where(e => e.SessionId == sessionId).ToList());

    public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>(Entries.Where(e => e.Timestamp >= since).Take(maxResults).ToList());
}

internal sealed class ConditionalToolExecutor(Func<(string Content, bool IsError)> resultFactory) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var (content, isError) = resultFactory();
        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = isError
        });
    }
}
