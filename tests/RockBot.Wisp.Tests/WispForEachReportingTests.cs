using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RockBot.Host;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

/// <summary>
/// How <c>for_each</c> steps (#779) show up outside the executor: hashes, the batch result, the
/// <c>spawn_wisps</c> outcome and the execution log.
/// </summary>
[TestClass]
public class WispForEachReportingTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // ── Hashes ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShapeHash_OfAWispWithoutForEach_IsUnchanged()
    {
        var definition = new WispDefinition
        {
            Description = "plain",
            Steps =
            [
                new WispStep
                {
                    Id = "get", Mode = StepMode.Direct, Gateway = GatewayType.Mcp,
                    Server = "calendar-mcp", Tool = "get_events",
                    Params = JsonDocument.Parse("""{"accountId":"x","timeZone":"UTC"}""").RootElement,
                    OnFailure = new OnFailureAction { Action = "skip_to", SkipTo = "sum" }
                },
                new WispStep { Id = "sum", Mode = StepMode.Llm, Prompt = "Summarize" }
            ]
        };

        Assert.AreEqual(ShapeHashBeforeForEach(definition), SpawnWispsExecutor.ComputeShapeHash(definition));
    }

    [TestMethod]
    public void ShapeHash_IncludesTheForEachFields()
    {
        var plain = Definition(ForEachStep() with { ForEach = null, Params = null });
        var iterated = Definition(ForEachStep());
        var moreConcurrent = Definition(ForEachStep() with { Concurrency = 4 });
        var otherSource = Definition(ForEachStep() with { ForEach = "{{steps.search.result.other}}" });
        var otherAccount = Definition(ForEachStep() with
        {
            Params = JsonDocument.Parse("""{"url":"{{item.id}}","account":"other"}""").RootElement
        });

        var hash = SpawnWispsExecutor.ComputeShapeHash(iterated);
        Assert.AreNotEqual(SpawnWispsExecutor.ComputeShapeHash(plain), hash);
        Assert.AreNotEqual(SpawnWispsExecutor.ComputeShapeHash(moreConcurrent), hash);
        Assert.AreNotEqual(SpawnWispsExecutor.ComputeShapeHash(otherSource), hash);
        // Param values still don't count, only keys: a new key changes the shape.
        Assert.AreNotEqual(SpawnWispsExecutor.ComputeShapeHash(otherAccount), hash);
        Assert.AreEqual(hash, SpawnWispsExecutor.ComputeShapeHash(Definition(ForEachStep() with
        {
            Params = JsonDocument.Parse("""{"url":"{{item.name}}"}""").RootElement
        })));
    }

    [TestMethod]
    public void DefinitionJson_OfAWispWithoutForEach_HasNoForEachKeys()
    {
        var json = JsonSerializer.Serialize(Definition(ForEachStep() with { ForEach = null }), CamelCase);

        foreach (var key in new[] { "for_each", "\"as\"", "max_items", "concurrency", "on_item_failure" })
            Assert.IsFalse(json.Contains(key), $"{key} is serialized: {json}");

        var iterated = JsonSerializer.Serialize(Definition(ForEachStep() with { Concurrency = 2 }), CamelCase);
        StringAssert.Contains(iterated, "\"for_each\":\"{{steps.search.result.items}}\"");
        StringAssert.Contains(iterated, "\"concurrency\":2");
        Assert.AreNotEqual(SpawnWispsExecutor.ComputeDefinitionHash(json), SpawnWispsExecutor.ComputeDefinitionHash(iterated));
    }

    // ── Batch result and outcome ─────────────────────────────────────────────

    [TestMethod]
    public void BatchResult_ContinuedPastFailedWrites_SaysSo()
    {
        var batch = Batch(IteratedCreates(failedIndices: [1, 3]));

        var formatted = SpawnWispsExecutor.FormatBatchResult(batch);

        StringAssert.StartsWith(formatted, "WRITES FAILED: 2 of 4 state-changing calls failed");
        StringAssert.Contains(formatted, "Step create: 2 of 4 items FAILED (on_item_failure: continue), indices 1, 3.");
        StringAssert.Contains(formatted, "[ok]");
    }

    [TestMethod]
    public void Outcome_ContinuedPastFailedWrites_IsAFailedCall()
    {
        var outcome = new ToolCallOutcome();

        SpawnWispsExecutor.ReportOutcome(outcome, Batch(IteratedCreates(failedIndices: [2])));

        Assert.AreEqual(false, outcome.Succeeded);
        StringAssert.Contains(outcome.Detail, "1 of 4 state-changing calls failed");
        Assert.AreEqual(4, outcome.NestedSnapshot().Count);
    }

    [TestMethod]
    public void BatchResult_AllItemsSucceeded_HasNoFailureLines()
    {
        var formatted = SpawnWispsExecutor.FormatBatchResult(Batch(IteratedCreates(failedIndices: [])));

        Assert.IsFalse(formatted.Contains("FAILED"), formatted);
    }

    [TestMethod]
    public void BatchResult_CapsALongIteratedOutput()
    {
        var longContent = "[" + string.Join(",", Enumerable.Range(0, 400).Select(i => $"{{\"index\":{i},\"ok\":true,\"result\":\"some text\"}}")) + "]";
        var step = IteratedCreates(failedIndices: []) with { Content = longContent };

        var formatted = SpawnWispsExecutor.FormatBatchResult(Batch(step));

        Assert.IsTrue(longContent.Length > SpawnWispsExecutor.MaxIteratedOutputChars);
        Assert.IsFalse(formatted.Contains(longContent));
        StringAssert.Contains(formatted, "truncated");
        StringAssert.Contains(formatted, "`wisp/wisp-1/create/output`");
    }

    [TestMethod]
    public void BatchResult_DoesNotCapAPlainStepsOutput()
    {
        var longContent = new string('x', SpawnWispsExecutor.MaxIteratedOutputChars + 100);
        var step = IteratedCreates(failedIndices: []) with { Content = longContent, ItemOutcome = null };

        StringAssert.Contains(SpawnWispsExecutor.FormatBatchResult(Batch(step)), longContent);
    }

    // ── Execution log ────────────────────────────────────────────────────────

    [TestMethod]
    public void IteratedSteps_SummarizesEachForEachStep_AndCapsTheIndices()
    {
        var failed = Enumerable.Range(0, 15).ToList();
        var step = new WispStepResult
        {
            StepId = "fetch", StepIndex = 1, IsSuccess = true, Duration = TimeSpan.Zero,
            ItemOutcome = new ForEachItemOutcome(20, 5, 15, 0, failed, true)
        };
        var result = Result(new WispStepResult { StepId = "search", StepIndex = 0, IsSuccess = true, Duration = TimeSpan.Zero }, step);

        var summary = SpawnWispsExecutor.SummarizeIteratedSteps(result)!;

        Assert.AreEqual(1, summary.Count);
        Assert.AreEqual("fetch", summary[0].StepId);
        Assert.AreEqual(20, summary[0].Total);
        Assert.AreEqual(5, summary[0].Succeeded);
        Assert.AreEqual(15, summary[0].Failed);
        Assert.AreEqual(WispIteratedStepSummary.MaxFailedIndices, summary[0].FailedIndices!.Count);
    }

    [TestMethod]
    public void IteratedSteps_IsNull_AndNotSerialized_ForAWispWithoutForEach()
    {
        var result = Result(new WispStepResult { StepId = "a", StepIndex = 0, IsSuccess = true, Duration = TimeSpan.Zero });
        Assert.IsNull(SpawnWispsExecutor.SummarizeIteratedSteps(result));

        var record = new WispExecutionRecord
        {
            WispId = "w", Description = "d", DefinitionHash = "h", Succeeded = true,
            StepCount = 1, StepsCompleted = 1, DurationMs = 1, Timestamp = DateTimeOffset.UnixEpoch
        };
        Assert.IsFalse(JsonSerializer.Serialize(record, CamelCase).Contains("iteratedSteps"));

        var withSteps = record with
        {
            IteratedSteps = [new WispIteratedStepSummary { StepId = "s", Total = 2, Succeeded = 2, Failed = 0 }]
        };
        var json = JsonSerializer.Serialize(withSteps, CamelCase);
        StringAssert.Contains(json, "\"iteratedSteps\":[{\"stepId\":\"s\",\"total\":2,\"succeeded\":2,\"failed\":0,\"skipped\":0}]");
        var back = JsonSerializer.Deserialize<WispExecutionRecord>(json, CamelCase)!;
        Assert.AreEqual(2, back.IteratedSteps![0].Total);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // The shape hash as computed before the for_each fields existed: an anonymous object.
    private static string ShapeHashBeforeForEach(WispDefinition definition)
    {
        static IReadOnlyList<string> Keys(JsonElement? e)
        {
            if (e is null || e.Value.ValueKind != JsonValueKind.Object) return [];
            var keys = e.Value.EnumerateObject().Select(p => p.Name).ToList();
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        var canonical = new
        {
            steps = definition.Steps.Select(s => new
            {
                id = s.Id,
                mode = s.Mode.ToString(),
                gateway = s.Gateway?.ToString(),
                server = s.Server,
                tool = s.Tool,
                language = s.Language,
                agent = s.Agent,
                skill = s.Skill,
                hasPrompt = !string.IsNullOrEmpty(s.Prompt),
                hasMessage = !string.IsNullOrEmpty(s.Message),
                paramKeys = Keys(s.ResolvedParams),
                metadataKeys = Keys(s.Metadata),
                hasInputFrom = !string.IsNullOrEmpty(s.InputFrom),
                hasOutputTo = !string.IsNullOrEmpty(s.OutputTo),
                onFailure = s.OnFailure?.Action.ToString()
            }).ToList()
        };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, CamelCase)));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static WispStep ForEachStep() => new()
    {
        Id = "fetch", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_browse",
        ForEach = "{{steps.search.result.items}}",
        Params = JsonDocument.Parse("""{"url":"{{item.id}}"}""").RootElement
    };

    private static WispDefinition Definition(WispStep step) => new()
    {
        Description = "d",
        Steps =
        [
            new WispStep { Id = "search", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_search" },
            step
        ]
    };

    // A for_each step that created four events under on_item_failure: continue.
    private static WispStepResult IteratedCreates(IReadOnlyList<int> failedIndices) => new()
    {
        StepId = "create", StepIndex = 0, IsSuccess = true, Duration = TimeSpan.Zero,
        Content = "[]",
        ToolCalls = Enumerable.Range(0, 4)
            .Select(i => new LoopToolCall("calendar-mcp__create_event", null, !failedIndices.Contains(i)))
            .ToList(),
        ItemOutcome = new ForEachItemOutcome(4, 4 - failedIndices.Count, failedIndices.Count, 0, failedIndices, true)
    };

    private static WispExecutionResult Result(params WispStepResult[] steps) => new()
    {
        WispId = "wisp-1", IsSuccess = steps.All(s => s.IsSuccess), Duration = TimeSpan.Zero,
        Definition = new WispDefinition { Description = "events", Steps = [] },
        StepResults = steps
    };

    private static WispBatchResult Batch(WispStepResult step) => new()
    {
        BatchId = "b",
        TotalDuration = TimeSpan.Zero,
        Results = [Result(step)]
    };
}
