using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

/// <summary>
/// <c>for_each</c> on direct steps (#779): one call per element of an earlier step's array, with
/// zero LLM tokens.
/// </summary>
[TestClass]
public class WispForEachTests
{
    private const string ThreeItems = """{"items":[{"id":"a","n":1},{"id":"b","n":2},{"id":"c","n":3}]}""";

    // ── Source resolution ────────────────────────────────────────────────────

    [TestMethod]
    public async Task EmptyList_SucceedsWithEmptyArray_AndMakesNoCalls()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, """{"items":[]}""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var step = result.StepResults[1];
        Assert.AreEqual("[]", step.Content);
        Assert.AreEqual(0, browse.Calls.Count);
        Assert.AreEqual(0, step.ItemOutcome!.Total);
        Assert.AreEqual(0, step.ItemOutcome.Failed);
        Assert.IsNull(step.ToolCalls);
    }

    [TestMethod]
    public async Task SourceIsAnObject_FailsAsData_PointingAtTheArrayField()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(
            Wisp(ForEachStep(forEach: "{{steps.search.result}}")), "wisp-fe", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Data, error.Category);
        StringAssert.Contains(error.Message, "not an array");
        StringAssert.Contains(error.Message, "items (array)");
        Assert.AreEqual(0, browse.Calls.Count);
    }

    [TestMethod]
    public async Task SourceIsNotJson_FailsAsData()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, "three results, in prose");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Data, error.Category);
        StringAssert.Contains(error.Message, "not JSON");
    }

    [TestMethod]
    public async Task SourcePathMissing_FailsAsData_ListingTheFields()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(
            Wisp(ForEachStep(forEach: "{{steps.search.result.messages}}")), "wisp-fe", CancellationToken.None);

        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Data, error.Category);
        StringAssert.Contains(error.Message, "'messages' was not found");
        StringAssert.Contains(error.Message, "items (array)");
    }

    [TestMethod]
    public async Task RootArraySource_IsIterated()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", r => Ok(Arg(r, "url")));
        RegisterSource(registry, """["https://a","https://b"]""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(
            forEach: "{{steps.search.result}}", paramsJson: """{"url":"{{item}}"}""")), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "https://a", "https://b" }, browse.Calls.Select(c => Arg(c, "url")).ToArray());
    }

    // ── Limits ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MoreItemsThanMaxItems_FailsBeforeAnyCall()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(maxItems: 2)), "wisp-fe", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Data, error.Category);
        StringAssert.Contains(error.Message, "has 3 items, more than this step's limit of 2");
        StringAssert.Contains(error.Message, "Nothing was run");
        Assert.AreEqual(0, browse.Calls.Count);
    }

    [TestMethod]
    public async Task MaxItems_IsClampedToTheCeiling()
    {
        var (executor, registry, _) = Create(new WispOptions { ForEachMaxItemsCeiling = 2 });
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(maxItems: 50)), "wisp-fe", CancellationToken.None);

        StringAssert.Contains(result.StepResults[1].Error!.Message, "limit of 2");
        StringAssert.Contains(result.StepResults[1].Error!.Message, "2 is the ceiling");
        Assert.AreEqual(0, browse.Calls.Count);
    }

    [TestMethod]
    public async Task DefaultMaxItems_ComesFromOptions()
    {
        var (executor, registry, _) = Create(new WispOptions { ForEachDefaultMaxItems = 2 });
        Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        StringAssert.Contains(result.StepResults[1].Error!.Message, "limit of 2");
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Continue_PartialFailure_StepSucceeds_AndListsTheFailedItem()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", r => Arg(r, "url") == "b" ? Fail("Page not found") : Ok($"page {Arg(r, "url")}"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(
            Wisp(ForEachStep(onItemFailure: "continue")), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var step = result.StepResults[1];
        Assert.IsTrue(step.IsSuccess);
        Assert.IsNull(step.Error);
        Assert.AreEqual(3, step.ItemOutcome!.Total);
        Assert.AreEqual(2, step.ItemOutcome.Succeeded);
        Assert.AreEqual(1, step.ItemOutcome.Failed);
        CollectionAssert.AreEqual(new[] { 1 }, step.ItemOutcome.FailedIndices.ToArray());

        var entries = JsonDocument.Parse(step.Content!).RootElement;
        Assert.AreEqual(3, entries.GetArrayLength());
        Assert.IsTrue(entries[0].GetProperty("ok").GetBoolean());
        Assert.AreEqual("page a", entries[0].GetProperty("result").GetString());
        Assert.IsFalse(entries[1].GetProperty("ok").GetBoolean());
        Assert.AreEqual("Page not found", entries[1].GetProperty("error").GetString());
        Assert.AreEqual(1, entries[1].GetProperty("index").GetInt32());
        Assert.AreEqual(3, step.ToolCalls!.Count);
        Assert.IsFalse(step.ToolCalls[1].Succeeded);
    }

    [TestMethod]
    public async Task Continue_EveryItemFails_StepFails()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", _ => Fail("service unavailable"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(
            Wisp(ForEachStep(onItemFailure: "continue")), "wisp-fe", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        var step = result.StepResults[1];
        Assert.AreEqual(FailureCategory.External, step.Error!.Category);
        StringAssert.StartsWith(step.Error.Message, "All 3 items failed. Item 0: service unavailable");
        Assert.AreEqual(3, step.ItemOutcome!.Failed);
    }

    [TestMethod]
    public async Task Abort_StopsLaunchingAfterTheFirstFailure()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", r => Arg(r, "url") == "b" ? Fail("Page not found") : Ok("ok"));
        RegisterSource(registry, """{"items":[{"id":"a"},{"id":"b"},{"id":"c"},{"id":"d"}]}""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(2, browse.Calls.Count, "items after the failed one were not launched");
        var step = result.StepResults[1];
        StringAssert.StartsWith(step.Error!.Message,
            "Item 1 of 4 failed (on_item_failure: abort; 1 succeeded, 2 not run): Page not found");
        Assert.AreEqual(2, step.ItemOutcome!.Skipped);

        var entries = JsonDocument.Parse(step.Content!).RootElement;
        Assert.IsTrue(entries[2].GetProperty("skipped").GetBoolean());
        Assert.IsTrue(entries[3].GetProperty("skipped").GetBoolean());
    }

    [TestMethod]
    public async Task FailedStep_StillWritesTheAggregateToWorkingMemory()
    {
        var (executor, registry, memory) = Create();
        Register(registry, "web_browse", r => Arg(r, "url") == "b" ? Fail("Page not found") : Ok("ok"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(result.StepResults[1].Content, memory.Store["wisp/wisp-fe/fetch/output"]);
    }

    [TestMethod]
    public async Task ACallThatThrows_IsAFailedCallOfThatItem()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", r => Arg(r, "url") == "b"
            ? throw new InvalidOperationException("connection reset")
            : Ok("ok"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(
            Wisp(ForEachStep(onItemFailure: "continue")), "wisp-fe", CancellationToken.None);

        var step = result.StepResults[1];
        Assert.IsTrue(step.IsSuccess);
        Assert.AreEqual(3, step.ToolCalls!.Count, "the call that threw is reported");
        Assert.IsFalse(step.ToolCalls[1].Succeeded);
        CollectionAssert.AreEqual(new[] { 1 }, step.ItemOutcome!.FailedIndices.ToArray());
        var entries = JsonDocument.Parse(step.Content!).RootElement;
        Assert.AreEqual("connection reset", entries[1].GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Cancellation_KeepsTheCompletedItemsOutcomesAndCalls()
    {
        var (executor, registry, memory) = Create();
        using var cts = new CancellationTokenSource();
        Register(registry, "web_browse", r =>
        {
            if (Arg(r, "url") == "b")
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            return Ok("ok");
        });
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", cts.Token);

        var step = result.StepResults[1];
        Assert.IsFalse(step.IsSuccess);
        Assert.AreEqual(FailureCategory.External, step.Error!.Category);
        StringAssert.StartsWith(step.Error.Message, "Step was cancelled after 1 of 3 items succeeded");
        Assert.AreEqual(1, step.ItemOutcome!.Succeeded);
        Assert.AreEqual(1, step.ItemOutcome.Skipped);
        Assert.IsTrue(step.ToolCalls![0].Succeeded, "the completed write is still reported");
        var entries = JsonDocument.Parse(step.Content!).RootElement;
        Assert.IsTrue(entries[0].GetProperty("ok").GetBoolean());
        Assert.AreEqual(step.Content, memory.Store["wisp/wisp-fe/fetch/output"]);
    }

    [TestMethod]
    public async Task ScalarJsonResults_KeepTheirTypes()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", r => Ok(Arg(r, "url") switch
        {
            "0" => "42",
            "1" => "true",
            "2" => "\"quoted\"",
            "3" => "null",
            _ => "plain text"
        }));
        RegisterSource(registry, """{"items":[0,1,2,3,4]}""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(paramsJson: """{"url":"{{item}}"}""")),
            "wisp-fe", CancellationToken.None);

        var entries = JsonDocument.Parse(result.StepResults[1].Content!).RootElement;
        Assert.AreEqual(42, entries[0].GetProperty("result").GetInt32());
        Assert.IsTrue(entries[1].GetProperty("result").GetBoolean());
        Assert.AreEqual("quoted", entries[2].GetProperty("result").GetString());
        Assert.AreEqual(JsonValueKind.Null, entries[3].GetProperty("result").ValueKind);
        Assert.AreEqual("plain text", entries[4].GetProperty("result").GetString());
    }

    [TestMethod]
    public async Task Abort_WithOnFailureSkipTo_LetsTheWispContinue()
    {
        var (executor, registry, _) = Create();
        Register(registry, "web_browse", _ => Fail("Page not found"));
        Register(registry, "execute_python_script", _ => Ok("fallback"));
        RegisterSource(registry, ThreeItems);

        var fetch = ForEachStep() with { OnFailure = new OnFailureAction { Action = "skip_to", SkipTo = "fallback" } };
        var fallback = new WispStep
        {
            Id = "fallback", Mode = StepMode.Direct, Gateway = GatewayType.Script,
            Params = Json("""{"script":"print('fallback')"}""")
        };

        var result = await executor.ExecuteAsync(Wisp(fetch, fallback), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.StepResults[1].FailureHandled);
        Assert.AreEqual("fallback", result.StepResults[2].Content);
    }

    // ── Concurrency and ordering ─────────────────────────────────────────────

    [TestMethod]
    public async Task UnderConcurrency_ResultsStayInInputOrder_AndInFlightIsBounded()
    {
        var (executor, registry, _) = Create();
        var inFlight = 0;
        var maxInFlight = 0;
        Register(registry, "web_browse", async r =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (registry) maxInFlight = Math.Max(maxInFlight, now);
            var n = int.Parse(Arg(r, "url"));
            await Task.Delay((6 - n) * 15); // earlier items finish last
            Interlocked.Decrement(ref inFlight);
            return Ok($"r{n}");
        });
        RegisterSource(registry, """{"items":[0,1,2,3,4,5]}""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(
            paramsJson: """{"url":"{{item}}"}""", concurrency: 3)), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var entries = JsonDocument.Parse(result.StepResults[1].Content!).RootElement;
        for (var i = 0; i < 6; i++)
        {
            Assert.AreEqual(i, entries[i].GetProperty("index").GetInt32());
            Assert.AreEqual($"r{i}", entries[i].GetProperty("result").GetString());
        }
        Assert.IsTrue(maxInFlight <= 3, $"max in flight was {maxInFlight}");
        Assert.IsTrue(maxInFlight > 1, "items ran concurrently");
    }

    [TestMethod]
    public async Task Concurrency_IsClampedToTheOptionsMaximum()
    {
        var (executor, registry, _) = Create(new WispOptions { ForEachMaxConcurrency = 2 });
        var inFlight = 0;
        var maxInFlight = 0;
        Register(registry, "web_browse", async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (registry) maxInFlight = Math.Max(maxInFlight, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref inFlight);
            return Ok("x");
        });
        RegisterSource(registry, """{"items":[0,1,2,3,4,5]}""");

        await executor.ExecuteAsync(Wisp(ForEachStep(concurrency: 10)), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(maxInFlight <= 2, $"max in flight was {maxInFlight}");
    }

    // ── Templating ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ItemTemplates_ResolveInsideNestedParams()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, """{"account":"work","items":[{"id":"a","meta":{"tag":"t1"}},{"id":"b","meta":{"tag":"t2"}}]}""");

        var result = await executor.ExecuteAsync(Wisp(ForEachStep(paramsJson: """
            {
              "url": "u",
              "filter": { "where": { "tag": "{{item.meta.tag}}", "account": "{{steps.search.result.account}}" } },
              "label": "n{{item_index}}",
              "raw": "{{item}}"
            }
            """)), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var second = JsonDocument.Parse(browse.Calls[1].Arguments!).RootElement;
        Assert.AreEqual("t2", second.GetProperty("filter").GetProperty("where").GetProperty("tag").GetString());
        Assert.AreEqual("work", second.GetProperty("filter").GetProperty("where").GetProperty("account").GetString());
        Assert.AreEqual("n1", second.GetProperty("label").GetString());
        var raw = JsonDocument.Parse(second.GetProperty("raw").GetString()!).RootElement;
        Assert.AreEqual("b", raw.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task AsAlias_ResolvesAlongsideItem()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        await executor.ExecuteAsync(Wisp(ForEachStep(
            paramsJson: """{"url":"{{msg.id}}","n":"{{item.n}}"}""", alias: "msg")), "wisp-fe", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, browse.Calls.Select(c => Arg(c, "url")).ToArray());
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, browse.Calls.Select(c => Arg(c, "n")).ToArray());
    }

    [TestMethod]
    public async Task UnresolvedItemPath_IsLeftLiteral()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, """{"items":[{"id":"a"}]}""");

        await executor.ExecuteAsync(Wisp(ForEachStep(paramsJson: """{"url":"{{item.missing}}"}""")),
            "wisp-fe", CancellationToken.None);

        Assert.AreEqual("{{item.missing}}", Arg(browse.Calls[0], "url"));
    }

    [TestMethod]
    public async Task SubstitutedText_IsNotReScannedAsATemplate()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, """{"note":"{{item}}","items":["{{steps.search.result.note}}"]}""");

        await executor.ExecuteAsync(Wisp(ForEachStep(paramsJson: """{"url":"{{item}}","note":"{{steps.search.result.note}}"}""")),
            "wisp-fe", CancellationToken.None);

        Assert.AreEqual("{{steps.search.result.note}}", Arg(browse.Calls[0], "url"));
        Assert.AreEqual("{{item}}", Arg(browse.Calls[0], "note"));
    }

    // ── Outputs ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task OnlyTheAggregate_IsWrittenToWorkingMemory()
    {
        // A wisp's working-memory namespace holds a limited number of entries (50 by default);
        // one entry per item would push out the aggregate and later steps' outputs.
        var (executor, registry, memory) = Create();
        Register(registry, "web_browse", r => Ok("""{"title":"T-""" + Arg(r, "url") + "\"}"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "wisp/wisp-fe/search/output", "wisp/wisp-fe/fetch/output" },
            memory.Store.Keys.ToArray());
        Assert.AreEqual(result.StepResults[1].Content, memory.Store["wisp/wisp-fe/fetch/output"]);
        // JSON results are embedded as JSON, not as strings.
        var entries = JsonDocument.Parse(result.StepResults[1].Content!).RootElement;
        Assert.AreEqual("T-c", entries[2].GetProperty("result").GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task EachItem_IsReportedAsItsOwnToolCall()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var result = await executor.ExecuteAsync(Wisp(ForEachStep()), "wisp-fe", CancellationToken.None);

        var calls = result.StepResults[1].ToolCalls!;
        Assert.AreEqual(3, calls.Count);
        Assert.AreEqual("wisp wisp-fe step fetch item 2", calls[2].Detail);
        CollectionAssert.AreEqual(
            new[] { "wisp-wisp-fe-fetch-0", "wisp-wisp-fe-fetch-1", "wisp-wisp-fe-fetch-2" },
            browse.Calls.Select(c => c.ToolCallId).ToArray());
    }

    // ── Gateways ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task McpGateway_RoutesEachItemThroughMcpInvokeTool()
    {
        var (executor, registry, _) = Create();
        var invoke = Register(registry, "mcp_invoke_tool", _ => Ok("""{"subject":"hi"}"""));
        RegisterSource(registry, ThreeItems);

        var fetch = new WispStep
        {
            Id = "fetch", Mode = StepMode.Direct, Gateway = GatewayType.Mcp,
            Server = "mail", Tool = "get_message",
            ForEach = "{{steps.search.result.items}}",
            Params = Json("""{"accountId":"work","messageId":"{{item.id}}"}""")
        };

        var result = await executor.ExecuteAsync(Wisp(fetch), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var ids = invoke.Calls
            .Select(c => JsonDocument.Parse(c.Arguments!).RootElement.GetProperty("arguments").GetProperty("messageId").GetString())
            .ToArray();
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, ids);
        Assert.AreEqual("mail__get_message", result.StepResults[1].ToolCalls![0].Name);
    }

    [TestMethod]
    public async Task A2AGateway_IsRejected_BecauseInvokeAgentAllowsOnePendingTaskPerSession()
    {
        var (executor, registry, _) = Create();
        var invoke = Register(registry, "invoke_agent", _ => Ok("dispatched"));
        RegisterSource(registry, ThreeItems);

        var ask = new WispStep
        {
            Id = "ask", Mode = StepMode.Direct, Gateway = GatewayType.A2A,
            Agent = "analyst", Skill = "summarize",
            ForEach = "{{steps.search.result.items}}",
            Message = "Summarize item {{item.id}}"
        };

        var result = await executor.ExecuteAsync(Wisp(ask), "wisp-fe", CancellationToken.None);

        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "isn't supported on A2A steps");
        Assert.AreEqual(0, invoke.Calls.Count);
    }

    [TestMethod]
    public async Task ScriptGateway_TemplatesTheScriptPerItem()
    {
        var (executor, registry, _) = Create();
        var script = Register(registry, "execute_python_script", _ => Ok("done"));
        RegisterSource(registry, ThreeItems);

        var run = new WispStep
        {
            Id = "run", Mode = StepMode.Direct, Gateway = GatewayType.Script,
            ForEach = "{{steps.search.result.items}}",
            Params = Json("""{"script":"print('{{item.id}}')"}""")
        };

        var result = await executor.ExecuteAsync(Wisp(run), "wisp-fe", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "print('a')", "print('b')", "print('c')" },
            script.Calls.Select(c => Arg(c, "script")).ToArray());
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ForEachOnAnLlmStep_IsStructural()
    {
        var (executor, registry, _) = Create();
        RegisterSource(registry, ThreeItems);
        var llm = new WispStep
        {
            Id = "think", Mode = StepMode.Llm, Prompt = "Summarize {{item}}",
            ForEach = "{{steps.search.result.items}}"
        };

        var result = await executor.ExecuteAsync(Wisp(llm), "wisp-fe", CancellationToken.None);

        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "only supported on Direct steps");
    }

    [TestMethod]
    public void ForEachReferencingALaterOrUnknownStep_IsStructural()
    {
        var fetch = ForEachStep(forEach: "{{steps.later.result.items}}");
        var later = new WispStep { Id = "later", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_search" };
        var definition = new WispDefinition { Description = "d", Steps = [fetch, later] };

        StringAssert.Contains(ForEachStepValidator.Validate(fetch, 0, definition)!.Message, "does not run before");

        var unknown = ForEachStep(forEach: "{{steps.nope.result}}");
        StringAssert.Contains(
            ForEachStepValidator.Validate(unknown, 0, new WispDefinition { Description = "d", Steps = [unknown] })!.Message,
            "not a step in this wisp");

        var self = ForEachStep(forEach: "{{steps.fetch.result}}");
        Assert.IsNotNull(ForEachStepValidator.Validate(self, 0, new WispDefinition { Description = "d", Steps = [self] }));
    }

    [TestMethod]
    [DataRow("{{steps.search.result.items}} and more", "single")]
    [DataRow("steps.search.result", "single")]
    public void ForEachThatIsNotASingleTemplate_IsStructural(string forEach, string expected)
    {
        var step = ForEachStep(forEach: forEach);
        var error = ForEachStepValidator.Validate(step, 1, Wisp(step));
        Assert.AreEqual(FailureCategory.Structural, error!.Category);
        StringAssert.Contains(error.Message, expected);
    }

    [TestMethod]
    [DataRow("1msg", "simple name")]
    [DataRow("steps", "reserved")]
    [DataRow("item_index", "reserved")]
    public void BadAlias_IsStructural(string alias, string expected)
    {
        var step = ForEachStep(alias: alias);
        StringAssert.Contains(ForEachStepValidator.Validate(step, 1, Wisp(step))!.Message, expected);
    }

    [TestMethod]
    public void BadIterationSettings_AreStructural()
    {
        var bad = ForEachStep(onItemFailure: "retry");
        StringAssert.Contains(ForEachStepValidator.Validate(bad, 1, Wisp(bad))!.Message, "'on_item_failure'");

        var zero = ForEachStep(maxItems: 0);
        StringAssert.Contains(ForEachStepValidator.Validate(zero, 1, Wisp(zero))!.Message, "'max_items'");

        var noConcurrency = ForEachStep(concurrency: 0);
        StringAssert.Contains(ForEachStepValidator.Validate(noConcurrency, 1, Wisp(noConcurrency))!.Message, "'concurrency'");

        var good = ForEachStep(alias: "msg", maxItems: 5, concurrency: 2, onItemFailure: "Continue");
        Assert.IsNull(ForEachStepValidator.Validate(good, 1, Wisp(good)));
    }

    [TestMethod]
    public async Task ItemTemplateWithoutForEach_IsStructural_AndNothingRuns()
    {
        var (executor, registry, _) = Create();
        var browse = Register(registry, "web_browse", _ => Ok("x"));
        RegisterSource(registry, ThreeItems);

        var step = ForEachStep() with { ForEach = null };
        var result = await executor.ExecuteAsync(Wisp(step), "wisp-fe", CancellationToken.None);

        var error = result.StepResults[1].Error!;
        Assert.AreEqual(FailureCategory.Structural, error.Category);
        StringAssert.Contains(error.Message, "has no 'for_each'");
        Assert.AreEqual(0, browse.Calls.Count);
    }

    [TestMethod]
    public void ItemTemplateInAnLlmPrompt_WithoutForEach_IsStructural()
    {
        var step = new WispStep { Id = "think", Mode = StepMode.Llm, Prompt = "Summarize {{item.id}}" };
        var error = ForEachStepValidator.Validate(step, 0, new WispDefinition { Description = "d", Steps = [step] });
        Assert.AreEqual(FailureCategory.Structural, error!.Category);
        StringAssert.Contains(error.Message, "has no 'for_each'");
    }

    [TestMethod]
    public void IterationSettingsWithoutForEach_AreStructural()
    {
        var step = new WispStep
        {
            Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_search",
            MaxItems = 5, OnItemFailure = "continue"
        };
        var error = ForEachStepValidator.Validate(step, 0, new WispDefinition { Description = "d", Steps = [step] });
        StringAssert.Contains(error!.Message, "sets max_items, on_item_failure but has no 'for_each'");
    }

    [TestMethod]
    public void ScriptWithItemTextAndNoForEach_IsLeftAlone()
    {
        var step = new WispStep
        {
            Id = "s", Mode = StepMode.Direct, Gateway = GatewayType.Script,
            Params = Json("""{"script":"from jinja2 import Template\nprint(Template('{{item}}').render(item=1))"}""")
        };
        Assert.IsNull(ForEachStepValidator.Validate(step, 0, new WispDefinition { Description = "d", Steps = [step] }));
    }

    [TestMethod]
    public void ForEachFields_RoundTripFromJson()
    {
        var step = JsonSerializer.Deserialize<WispStep>("""
            { "id": "fetch", "mode": "Direct", "gateway": "Mcp", "tool": "mail__get_message",
              "for_each": "{{steps.search.result.messages}}", "as": "msg",
              "max_items": 10, "concurrency": 2, "on_item_failure": "continue" }
            """)!;

        Assert.AreEqual("{{steps.search.result.messages}}", step.ForEach);
        Assert.AreEqual("msg", step.As);
        Assert.AreEqual(10, step.MaxItems);
        Assert.AreEqual(2, step.Concurrency);
        Assert.AreEqual("continue", step.OnItemFailure);
        Assert.IsTrue(step.IsIterated);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (WispExecutor Executor, FakeToolRegistry Registry, ConcurrentWorkingMemory Memory) Create(
        WispOptions? options = null)
    {
        var registry = new FakeToolRegistry();
        var memory = new ConcurrentWorkingMemory();
        var executor = new WispExecutor(registry, memory, agentLoopRunner: null!, options ?? new WispOptions(),
            NullLogger<WispExecutor>.Instance);
        return (executor, registry, memory);
    }

    private static void RegisterSource(FakeToolRegistry registry, string content) =>
        registry.Register(new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor(content));

    private static DelegateToolExecutor Register(
        FakeToolRegistry registry, string name, Func<ToolInvokeRequest, ToolInvokeResponse> respond) =>
        Register(registry, name, r => Task.FromResult(respond(r)));

    private static DelegateToolExecutor Register(
        FakeToolRegistry registry, string name, Func<ToolInvokeRequest, Task<ToolInvokeResponse>> respond)
    {
        var executor = new DelegateToolExecutor(respond);
        registry.Register(new ToolRegistration { Name = name, Description = name, Source = "test" }, executor);
        return executor;
    }

    private static ToolInvokeResponse Ok(string content) =>
        new() { ToolCallId = "c", ToolName = "t", Content = content };

    private static ToolInvokeResponse Fail(string error) =>
        new() { ToolCallId = "c", ToolName = "t", Content = error, IsError = true };

    private static string Arg(ToolInvokeRequest request, string name)
    {
        var value = JsonDocument.Parse(request.Arguments!).RootElement.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static WispStep SearchStep() => new()
    {
        Id = "search", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_search",
        Params = Json("""{"query":"q"}""")
    };

    private static WispStep ForEachStep(
        string forEach = "{{steps.search.result.items}}",
        string paramsJson = """{"url":"{{item.id}}"}""",
        string? alias = null,
        int? maxItems = null,
        int? concurrency = null,
        string? onItemFailure = null) => new()
    {
        Id = "fetch", Mode = StepMode.Direct, Gateway = GatewayType.Web, Tool = "web_browse",
        ForEach = forEach, As = alias, MaxItems = maxItems, Concurrency = concurrency,
        OnItemFailure = onItemFailure,
        Params = Json(paramsJson)
    };

    private static WispDefinition Wisp(params WispStep[] after) =>
        new() { Description = "for_each test", Steps = [SearchStep(), .. after] };
}

internal sealed class DelegateToolExecutor(Func<ToolInvokeRequest, Task<ToolInvokeResponse>> respond) : IToolExecutor
{
    private readonly ConcurrentQueue<ToolInvokeRequest> _calls = new();

    /// <summary>The requests received, in the order they started.</summary>
    public IReadOnlyList<ToolInvokeRequest> Calls => _calls.ToList();

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        _calls.Enqueue(request);
        var response = await respond(request);
        return response with { ToolCallId = request.ToolCallId, ToolName = request.ToolName };
    }
}

/// <summary>A working memory double that tolerates the concurrent writes of a for_each step.</summary>
internal sealed class ConcurrentWorkingMemory : IWorkingMemory
{
    public ConcurrentDictionary<string, string> Store { get; } = new();

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
        Store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        foreach (var key in Store.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            Store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
}
