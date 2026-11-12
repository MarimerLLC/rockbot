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

    // ── GetCanonicalBodyAsync (Phase 1: skill-asset promotion) ───────────────

    [TestMethod]
    public async Task GetCanonicalBodyAsync_SuccessfulRecord_ReturnsBody()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var body = """{"description":"x","steps":[]}""";

            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-ok",
                Description = "x",
                DefinitionHash = "hash-ok",
                Succeeded = true,
                StepCount = 0,
                StepsCompleted = 0,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow,
                DefinitionBody = body
            });

            var result = await log.GetCanonicalBodyAsync("hash-ok");
            Assert.AreEqual(body, result);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task GetCanonicalBodyAsync_FailedRecord_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            // Failed record: even if a body were stored, it would be null per executor policy.
            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-bad",
                Description = "x",
                DefinitionHash = "hash-bad",
                Succeeded = false,
                StepCount = 1,
                StepsCompleted = 0,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow,
                DefinitionBody = null
            });

            var result = await log.GetCanonicalBodyAsync("hash-bad");
            Assert.IsNull(result);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task GetCanonicalBodyAsync_TwoSuccessesSameHash_ReturnsFirstBody()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var body = """{"description":"dup","steps":[]}""";
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
            var later = DateTimeOffset.UtcNow;

            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "first",
                Description = "dup",
                DefinitionHash = "h-dup",
                Succeeded = true,
                StepCount = 0,
                StepsCompleted = 0,
                DurationMs = 5,
                Timestamp = earlier,
                DefinitionBody = body
            });

            // Second success of same hash — second-time executors may omit the body to dedup.
            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "second",
                Description = "dup",
                DefinitionHash = "h-dup",
                Succeeded = true,
                StepCount = 0,
                StepsCompleted = 0,
                DurationMs = 6,
                Timestamp = later,
                DefinitionBody = null
            });

            var result = await log.GetCanonicalBodyAsync("h-dup");
            Assert.AreEqual(body, result);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task GetCanonicalBodyAsync_OversizeBodyOmittedFlag_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);

            await log.AppendAsync(new WispExecutionRecord
            {
                WispId = "wisp-big",
                Description = "big",
                DefinitionHash = "h-big",
                Succeeded = true,
                StepCount = 1,
                StepsCompleted = 1,
                DurationMs = 5,
                Timestamp = DateTimeOffset.UtcNow,
                DefinitionBody = null,
                BodyOmittedTooLarge = true
            });

            var result = await log.GetCanonicalBodyAsync("h-big");
            Assert.IsNull(result);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task GetCanonicalBodyAsync_UnknownHash_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var result = await log.GetCanonicalBodyAsync("nope");
            Assert.IsNull(result);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    // ── SpawnWispsExecutor body retention ────────────────────────────────────

    [TestMethod]
    public async Task SpawnWispsExecutor_SuccessfulRun_PersistsDefinitionBody()
    {
        var tempDir = CreateTempDir();
        try
        {
            var executionLog = CreateLog(tempDir);
            var registry = new FakeToolRegistry();
            var memory = new FakeWorkingMemory();
            var options = new WispOptions();
            var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
                NullLogger<WispExecutor>.Instance);
            var spawnExecutor = new SpawnWispsExecutor(wispExecutor, executionLog, feedbackStore: null,
                memory, options, NullLogger<SpawnWispsExecutor>.Instance);

            registry.Register(
                new ToolRegistration { Name = "web_search", Description = "ok", Source = "web" },
                new ConditionalToolExecutor(() => ("done", false)));

            var defJson = """
            {
              "definitions": [
                {
                  "description": "Smoke test",
                  "steps": [{"id":"s1","mode":"Direct","gateway":"Web","tool":"web_search","params":{"query":"x"}}]
                }
              ]
            }
            """;

            var req = new ToolInvokeRequest { ToolCallId = "tc", ToolName = "spawn_wisps", Arguments = defJson, SessionId = "sess" };
            var resp = await spawnExecutor.ExecuteAsync(req, CancellationToken.None);
            Assert.IsFalse(resp.IsError, resp.Content);

            await Task.Delay(100);

            var records = await executionLog.QueryRecentAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.AreEqual(1, records.Count);
            Assert.IsTrue(records[0].Succeeded, $"record errored: {records[0].FailureCategory}: {records[0].ErrorMessage}");
            Assert.IsNotNull(records[0].DefinitionBody);
            Assert.IsFalse(records[0].BodyOmittedTooLarge);

            // Body should round-trip back through GetCanonicalBodyAsync
            var canonical = await executionLog.GetCanonicalBodyAsync(records[0].DefinitionHash);
            Assert.AreEqual(records[0].DefinitionBody, canonical);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task SpawnWispsExecutor_FailedRun_OmitsDefinitionBody()
    {
        var tempDir = CreateTempDir();
        try
        {
            var executionLog = CreateLog(tempDir);
            var registry = new FakeToolRegistry();
            var memory = new FakeWorkingMemory();
            var options = new WispOptions();
            var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
                NullLogger<WispExecutor>.Instance);
            var spawnExecutor = new SpawnWispsExecutor(wispExecutor, executionLog, feedbackStore: null,
                memory, options, NullLogger<SpawnWispsExecutor>.Instance);

            registry.Register(
                new ToolRegistration { Name = "web_search", Description = "fail", Source = "web" },
                new ConditionalToolExecutor(() => ("boom", true)));

            var defJson = """
            {
              "definitions": [
                {
                  "description": "Should fail",
                  "steps": [{"id":"s1","mode":"Direct","gateway":"Web","tool":"web_search","params":{"query":"x"}}]
                }
              ]
            }
            """;

            var req = new ToolInvokeRequest { ToolCallId = "tc", ToolName = "spawn_wisps", Arguments = defJson, SessionId = "sess" };
            await spawnExecutor.ExecuteAsync(req, CancellationToken.None);

            await Task.Delay(100);

            var records = await executionLog.QueryRecentAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.AreEqual(1, records.Count);
            Assert.IsFalse(records[0].Succeeded);
            Assert.IsNull(records[0].DefinitionBody);
            Assert.IsFalse(records[0].BodyOmittedTooLarge);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    // ── Incremental cache (#465) ─────────────────────────────────────────────

    [TestMethod]
    public async Task Queries_AfterAppend_ParseOnlyNewLines_AsLogGrows()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var t0 = DateTimeOffset.UtcNow.AddHours(-1);

            for (var i = 0; i < 2000; i++)
                await log.AppendAsync(MakeRecord($"seed-{i}", $"hash-{i % 50}", succeeded: i % 3 != 0, t0.AddSeconds(i)));

            await log.FindRecentFailureAsync("hash-1", null);
            Assert.AreEqual(2000, log.LinesParsed, "first query loads the whole file once");

            // The hot path: one append then the retry-detection query, repeated. Each
            // round must parse exactly the one new line regardless of file size.
            for (var i = 0; i < 50; i++)
            {
                var before = log.LinesParsed;
                await log.AppendAsync(MakeRecord($"new-{i}", "hash-1", succeeded: true, t0.AddSeconds(3000 + i)));
                await log.FindRecentFailureAsync("hash-1", null);
                await log.QueryRecentAsync(t0, 1000);
                await log.GetCanonicalBodyAsync("hash-1");
                Assert.AreEqual(before + 1, log.LinesParsed, $"round {i} re-parsed old lines");
            }

            var all = await log.QueryRecentAsync(t0.AddSeconds(-1), int.MaxValue);
            Assert.AreEqual(2050, all.Count);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Query_SeesLinesAppendedByAnotherWriter()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            await log.AppendAsync(MakeRecord("w1", "h", succeeded: true, DateTimeOffset.UtcNow));
            Assert.AreEqual(1, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 10)).Count);

            await File.AppendAllTextAsync(LogPath(tempDir),
                Serialize(MakeRecord("w2", "h", succeeded: false, DateTimeOffset.UtcNow)) + "\n");

            var failure = await log.FindRecentFailureAsync("h", null);
            Assert.AreEqual("w2", failure?.WispId);
            Assert.AreEqual(2, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 10)).Count);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Query_FileReplacedWithLongerContent_Reloads()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            await log.AppendAsync(MakeRecord("old-1", "h", succeeded: false, DateTimeOffset.UtcNow));
            Assert.AreEqual("old-1", (await log.FindRecentFailureAsync("h", null))?.WispId);

            // Replace the file (as a trim by another instance would) with different,
            // longer content, so the length alone cannot reveal the swap.
            var lines = Enumerable.Range(0, 3)
                .Select(i => Serialize(MakeRecord($"replacement-{i}", "h2", succeeded: true, DateTimeOffset.UtcNow)));
            var tmp = LogPath(tempDir) + ".tmp";
            await File.WriteAllLinesAsync(tmp, lines);
            File.Move(tmp, LogPath(tempDir), overwrite: true);

            var all = await log.QueryRecentAsync(DateTimeOffset.MinValue, 10);
            CollectionAssert.AreEqual(
                new[] { "replacement-0", "replacement-1", "replacement-2" },
                all.Select(r => r.WispId).ToArray());
            Assert.IsNull(await log.FindRecentFailureAsync("h", null));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    [DataRow(3, DisplayName = "same length")]
    [DataRow(5, DisplayName = "longer")]
    public async Task Query_FileReplacedKeepingFirstRecord_Reloads(int replacementCount)
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var ts = DateTimeOffset.UtcNow;
            var first = MakeRecord("w0", "h", succeeded: true, ts);
            await log.AppendAsync(first);
            for (var i = 1; i <= 3; i++)
                await log.AppendAsync(MakeRecord($"w{i}", "h", succeeded: false, ts));
            Assert.AreEqual(4, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 100)).Count);

            // Keep the first record (so the head bytes match) but rewrite everything after
            // it. Equal-length ids make the "same length" case byte-for-byte the same size.
            var lines = new[] { Serialize(first) }
                .Concat(Enumerable.Range(1, replacementCount)
                    .Select(i => Serialize(MakeRecord($"x{i}", "h", succeeded: false, ts))));
            var tmp = LogPath(tempDir) + ".tmp";
            await File.WriteAllTextAsync(tmp, string.Concat(lines.Select(l => l + Environment.NewLine)));
            File.Move(tmp, LogPath(tempDir), overwrite: true);

            var expected = new[] { "w0" }
                .Concat(Enumerable.Range(1, replacementCount).Select(i => $"x{i}"))
                .ToArray();
            CollectionAssert.AreEqual(
                expected,
                (await log.QueryRecentAsync(DateTimeOffset.MinValue, 100)).Select(r => r.WispId).ToArray());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task PruneAsync_TrimsFile_QueriesReflectTrimAndLaterAppends()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
            for (var i = 0; i < 10; i++)
                await log.AppendAsync(MakeRecord($"w{i}", "h", succeeded: false, t0.AddSeconds(i)));
            Assert.AreEqual(10, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 100)).Count);

            var removed = await log.PruneAsync(new LogRetentionPolicy(TimeSpan.Zero, 0, MaxLinesPerFile: 3));
            Assert.AreEqual(7, removed);

            CollectionAssert.AreEqual(
                new[] { "w7", "w8", "w9" },
                (await log.QueryRecentAsync(DateTimeOffset.MinValue, 100)).Select(r => r.WispId).ToArray());

            await log.AppendAsync(MakeRecord("w10", "h", succeeded: false, t0.AddSeconds(10)));
            CollectionAssert.AreEqual(
                new[] { "w7", "w8", "w9", "w10" },
                (await log.QueryRecentAsync(DateTimeOffset.MinValue, 100)).Select(r => r.WispId).ToArray());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Query_PartialTrailingLine_IgnoredUntilComplete()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            await log.AppendAsync(MakeRecord("w1", "h", succeeded: true, DateTimeOffset.UtcNow));

            var json = Serialize(MakeRecord("w2", "h", succeeded: true, DateTimeOffset.UtcNow));
            var half = json.Length / 2;
            await File.AppendAllTextAsync(LogPath(tempDir), json[..half]);
            Assert.AreEqual(1, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 10)).Count);

            await File.AppendAllTextAsync(LogPath(tempDir), json[half..] + "\n");
            var all = await log.QueryRecentAsync(DateTimeOffset.MinValue, 10);
            CollectionAssert.AreEqual(new[] { "w1", "w2" }, all.Select(r => r.WispId).ToArray());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Query_MalformedLine_SkippedAndNotReparsed()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            await log.AppendAsync(MakeRecord("w1", "h", succeeded: true, DateTimeOffset.UtcNow));
            await File.AppendAllTextAsync(LogPath(tempDir), "{ this is not json\n");
            await log.AppendAsync(MakeRecord("w2", "h", succeeded: true, DateTimeOffset.UtcNow));

            var all = await log.QueryRecentAsync(DateTimeOffset.MinValue, 10);
            CollectionAssert.AreEqual(new[] { "w1", "w2" }, all.Select(r => r.WispId).ToArray());

            var parsed = log.LinesParsed;
            await log.QueryRecentAsync(DateTimeOffset.MinValue, 10);
            Assert.AreEqual(parsed, log.LinesParsed);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task Query_FileDeleted_ReturnsEmpty_ThenAppendWorks()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            await log.AppendAsync(MakeRecord("w1", "h", succeeded: false, DateTimeOffset.UtcNow));
            Assert.AreEqual(1, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 10)).Count);

            File.Delete(LogPath(tempDir));
            Assert.AreEqual(0, (await log.QueryRecentAsync(DateTimeOffset.MinValue, 10)).Count);
            Assert.IsNull(await log.FindRecentFailureAsync("h", null));

            await log.AppendAsync(MakeRecord("w2", "h", succeeded: false, DateTimeOffset.UtcNow));
            Assert.AreEqual("w2", (await log.FindRecentFailureAsync("h", null))?.WispId);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task QueryByHashAsync_MoreThanCapOfOtherRecordsInWindow_ReturnsEveryMatchIncludingNewest()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1,200 unrelated records older than the matches: a capped, oldest-first
            // QueryRecentAsync(maxResults: 1000) would return none of the matches (#791).
            var t0 = DateTimeOffset.UtcNow.AddDays(-10);
            var seed = Enumerable.Range(0, 1200)
                .Select(i => Serialize(MakeRecord($"other-{i}", "other", succeeded: true, t0.AddSeconds(i))));
            await File.WriteAllLinesAsync(LogPath(tempDir), seed);

            var log = CreateLog(tempDir);
            var t1 = DateTimeOffset.UtcNow.AddMinutes(-5);
            await log.AppendAsync(MakeRecord("match-0", "target", succeeded: true, t1) with { ShapeHash = "shape-a" });
            await log.AppendAsync(MakeRecord("match-1", "target-2", succeeded: true, t1.AddSeconds(1)) with { ShapeHash = "shape-a" });

            Assert.IsFalse(
                (await log.QueryRecentAsync(DateTimeOffset.UtcNow.AddDays(-30), 1000)).Any(r => r.ShapeHash == "shape-a"),
                "Precondition: the capped query loses the newest records");

            var byShape = await log.QueryByHashAsync("shape-a", DateTimeOffset.UtcNow.AddDays(-30));
            CollectionAssert.AreEqual(new[] { "match-0", "match-1" }, byShape.Select(r => r.WispId).ToArray());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task QueryByHashAsync_MatchesShapeOrDefinitionHash_OnceEach_AscendingAndSince()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var now = DateTimeOffset.UtcNow;
            await log.AppendAsync(MakeRecord("too-old", "h", succeeded: true, now.AddDays(-40)));
            await log.AppendAsync(MakeRecord("by-def-later", "h", succeeded: true, now.AddMinutes(-1)));
            await log.AppendAsync(MakeRecord("by-shape", "other", succeeded: false, now.AddMinutes(-3)) with { ShapeHash = "h" });
            // Shape and definition hash both equal the query: returned once, not twice.
            await log.AppendAsync(MakeRecord("both", "h", succeeded: true, now.AddMinutes(-2)) with { ShapeHash = "h" });
            await log.AppendAsync(MakeRecord("unrelated", "x", succeeded: true, now) with { ShapeHash = "y" });

            var result = await log.QueryByHashAsync("H", now.AddDays(-30));
            CollectionAssert.AreEqual(
                new[] { "by-shape", "both", "by-def-later" },
                result.Select(r => r.WispId).ToArray());

            Assert.AreEqual(0, (await log.QueryByHashAsync("", DateTimeOffset.MinValue)).Count);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task QueryByHashAsync_AfterPrune_ReflectsTrimmedFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var log = CreateLog(tempDir);
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
            for (var i = 0; i < 5; i++)
                await log.AppendAsync(MakeRecord($"w{i}", "d", succeeded: true, t0.AddSeconds(i)) with { ShapeHash = "s" });
            Assert.AreEqual(5, (await log.QueryByHashAsync("s", DateTimeOffset.MinValue)).Count);

            await log.PruneAsync(new LogRetentionPolicy(TimeSpan.Zero, 0, MaxLinesPerFile: 2));

            CollectionAssert.AreEqual(
                new[] { "w3", "w4" },
                (await log.QueryByHashAsync("s", DateTimeOffset.MinValue)).Select(r => r.WispId).ToArray());
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [TestMethod]
    public async Task QueryByHashAsync_DefaultInterfaceImplementation_IsUncapped()
    {
        IWispExecutionLog log = new ListBackedWispLog(
            Enumerable.Range(0, 1200)
                .Select(i => MakeRecord($"other-{i}", "other", succeeded: true, DateTimeOffset.UtcNow.AddDays(-5).AddSeconds(i)))
                .Append(MakeRecord("match", "d", succeeded: true, DateTimeOffset.UtcNow) with { ShapeHash = "S" })
                .ToList());

        var result = await log.QueryByHashAsync("s", DateTimeOffset.UtcNow.AddDays(-30));
        Assert.AreEqual("match", result.Single().WispId);
    }

    /// <summary>Implements only the required members, so the default QueryByHashAsync runs.</summary>
    private sealed class ListBackedWispLog(List<WispExecutionRecord> records) : IWispExecutionLog
    {
        public Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
            DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WispExecutionRecord>>(
                records.Where(r => r.Timestamp >= since).OrderBy(r => r.Timestamp).Take(maxResults).ToList());

        public Task<WispExecutionRecord?> FindRecentFailureAsync(
            string definitionHash, string? sessionId, CancellationToken ct = default) =>
            Task.FromResult<WispExecutionRecord?>(null);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WispExecutionRecord MakeRecord(string wispId, string hash, bool succeeded, DateTimeOffset timestamp) =>
        new()
        {
            WispId = wispId,
            Description = "test",
            DefinitionHash = hash,
            Succeeded = succeeded,
            StepCount = 1,
            StepsCompleted = succeeded ? 1 : 0,
            DurationMs = 10,
            Timestamp = timestamp,
            DefinitionBody = succeeded ? "{\"steps\":[]}" : null
        };

    private static string Serialize(WispExecutionRecord record) =>
        JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static string LogPath(string basePath) => Path.Combine(basePath, "wisp-executions.jsonl");

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
