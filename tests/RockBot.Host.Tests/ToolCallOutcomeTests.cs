using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

/// <summary>
/// A tool's own report of its outcome (#686). On 2026-10-10 a <c>spawn_wisps</c> batch in which 6
/// of 7 wisps aborted was logged as one successful call. Its subagent then reported "seven prep
/// blocks scheduled and verified" when one event existed. A batch tool now reports the failure and
/// the calls it made through <see cref="ToolCallOutcomeContext"/>. The ledger and the tool-call log
/// record them, and the result the model sees stays unchanged.
/// </summary>
[TestClass]
public class ToolCallOutcomeTests
{
    private const string BatchResult = "PARTIAL FAILURE: 6 of 7 wisps failed. 7 wisp(s) completed (1 succeeded, 6 failed)";

    private static readonly LoopToolCall[] SevenCreates =
    [
        new("calendar-mcp__create_event", "title=Prep 1", true) { Detail = "wisp wisp-0001 step create" },
        .. Enumerable.Range(2, 6).Select(i =>
            new LoopToolCall("calendar-mcp__create_event", $"title=Prep {i}", false) { Detail = $"wisp wisp-000{i} step create" }),
    ];

    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task ReportedFailure_IsRecordedAsFailedWithNestedCalls_ResultUnchanged(bool textBased)
    {
        var log = new RecordingToolCallLog();
        var (ledger, toolResult) = await RunAsync(textBased, outcome =>
        {
            outcome.AddNested(SevenCreates);
            outcome.Report(succeeded: false, detail: "6 of 7 wisps failed");
        }, log);

        var call = ledger.Single();
        Assert.AreEqual("spawn_wisps", call.Name);
        Assert.IsFalse(call.Succeeded, "a batch with failed wisps is a failed call");
        Assert.AreEqual("6 of 7 wisps failed", call.Detail);
        Assert.AreEqual(7, call.Nested!.Count);
        Assert.AreEqual(1, call.Nested.Count(n => n.Succeeded));
        Assert.IsTrue(ToolSideEffects.IsSideEffecting(call), "its nested creates change state");

        Assert.AreEqual(BatchResult, toolResult, "the model sees the result as is — not an error to retry");
        Assert.IsFalse(toolResult.StartsWith("Error:", StringComparison.Ordinal));

        if (!textBased)
        {
            var evt = log.Events.Single();
            Assert.IsFalse(evt.Succeeded);
            Assert.AreEqual("6 of 7 wisps failed", evt.Detail);
            Assert.AreEqual("6 of 7 wisps failed", evt.ErrorMessage);
            Assert.AreEqual(7, evt.NestedCalls!.Count);
            Assert.AreEqual("calendar-mcp__create_event FAILED (wisp wisp-0002 step create)", evt.NestedCalls[1]);
        }
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task ReportedSuccess_KeepsNestedCalls(bool textBased)
    {
        var (ledger, _) = await RunAsync(textBased, outcome =>
        {
            outcome.AddNested([SevenCreates[0]]);
            outcome.Report(succeeded: true, detail: "all 1 wisps succeeded");
        });

        var call = ledger.Single();
        Assert.IsTrue(call.Succeeded);
        Assert.AreEqual(1, call.Nested!.Count);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task ToolThatReportsNothing_IsUnchanged(bool textBased)
    {
        var (ledger, _) = await RunAsync(textBased, _ => { });

        var call = ledger.Single();
        Assert.IsTrue(call.Succeeded);
        Assert.IsNull(call.Detail);
        Assert.IsNull(call.Nested);
        Assert.IsFalse(ToolSideEffects.IsSideEffecting(call), "spawn_wisps alone changes nothing");
    }

    [TestMethod]
    public void Context_IsNullOutsideAToolCall_AndRestoredAfterTheScope()
    {
        Assert.IsNull(ToolCallOutcomeContext.Value);
        var outer = new ToolCallOutcome();
        using (ToolCallOutcomeContext.Set(outer))
        {
            using (ToolCallOutcomeContext.Set(new ToolCallOutcome()))
                Assert.AreNotSame(outer, ToolCallOutcomeContext.Value);
            Assert.AreSame(outer, ToolCallOutcomeContext.Value);
        }
        Assert.IsNull(ToolCallOutcomeContext.Value);
    }

    [TestMethod]
    public void FormatNested_OneLinePerCall()
    {
        Assert.IsNull(ToolCallOutcome.FormatNested(null));
        Assert.IsNull(ToolCallOutcome.FormatNested([]));
        CollectionAssert.AreEqual(
            new[] { "calendar-mcp__create_event ok (wisp wisp-0001 step create)", "file_write FAILED" },
            ToolCallOutcome.FormatNested([SevenCreates[0], new LoopToolCall("file_write", null, false)])!.ToArray());
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<(IReadOnlyList<LoopToolCall> Ledger, string ToolResult)> RunAsync(
        bool textBased, Action<ToolCallOutcome> report, IToolCallLog? log = null)
    {
        var model = new ConsequentialActionGateTests.ToolThenTextChatClient(
            new FunctionCallContent("call-1", "spawn_wisps", new Dictionary<string, object?> { ["count"] = 7 }),
            "Reply.");
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((int? count = null) =>
            {
                // A real batch tool reports through the ambient holder the loop binds around the call.
                var outcome = ToolCallOutcomeContext.Value;
                Assert.IsNotNull(outcome, "the loop binds an outcome holder around every tool call");
                report(outcome);
                return BatchResult;
            }, "spawn_wisps")]
        };
        var diagnostics = new LoopDiagnostics();
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased, new AgentHostOptions(), log);

        await runner.RunAsync(
            [new(ChatRole.System, "You are a test agent."), new(ChatRole.User, "Schedule seven prep blocks.")],
            options, "s1", enableFollowUp: false, enableCompletionEval: false, diagnostics: diagnostics,
            cancellationToken: CancellationToken.None);

        // The tool-call log append is fire-and-forget.
        if (log is RecordingToolCallLog recording)
            await recording.WaitForAsync(1);

        return (diagnostics.ToolCallLedger?.Snapshot() ?? [], model.ToolResultSeen ?? string.Empty);
    }

    private sealed class RecordingToolCallLog : IToolCallLog
    {
        private readonly List<ToolCallEvent> _events = [];

        public IReadOnlyList<ToolCallEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default)
        {
            lock (_events) _events.Add(evt);
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            for (var i = 0; i < 100 && Events.Count < count; i++)
                await Task.Delay(10);
        }

        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Events);

        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult(Events);
    }
}
