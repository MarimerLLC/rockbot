extern alias fixture;

using System.Text.Json;
using fixture::McpServer.ElicitationFixture.Watches;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Watches;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The user's instructions for watches (#853) end to end: a watch's own <c>on_change</c>, the
/// standing watch rules, the warning before a watch's maximum lifetime, and the session's own
/// call's echo — on a real bridge and a real listen stream (<see cref="WatchableResources"/>).
/// </summary>
[TestClass]
public class McpWatchInstructionsEndToEndTests
{
    private const string Session = "session/test";
    private const string Other = "session/other";

    private static readonly string Poll = WatchableResources.UriFor("poll");

    private static Task<BridgeHarness> StartAsync(
        WatchableResources store,
        Action<McpBridgeOptions>? configureBridge = null,
        TimeProvider? timeProvider = null,
        RockBot.Host.IWorkingMemory? workingMemory = null) =>
        BridgeHarness.StartAsync(
            workingMemory: workingMemory,
            tools:
            [
                .. store.Tools(),
                // Changes a resource although the server calls it read-only: a call it marks so isn't an echo's cause.
                McpServerTool.Create(
                    (string name, string text) => { store.Set(name, text); return "ok"; },
                    new McpServerToolCreateOptions { Name = "peek_watch_resource", ReadOnly = true }),
                // An aggregator's two ways of reaching a downstream server's tool (#856). Registered
                // a second time as "aggregator", the fixture stands in for one in front of itself.
                McpServerTool.Create(
                    (string serverName, string toolName, JsonElement arguments) =>
                    {
                        var args = arguments.ValueKind == JsonValueKind.String
                            ? JsonDocument.Parse(arguments.GetString()!).RootElement
                            : arguments;
                        store.Set(args.GetProperty("name").GetString()!, args.GetProperty("text").GetString()!);
                        return $"ok ({serverName}/{toolName})";
                    },
                    new McpServerToolCreateOptions { Name = "invoke_tool" }),
                McpServerTool.Create(
                    (string name, string text) => { store.Set(name, text); return "ok"; },
                    new McpServerToolCreateOptions { Name = $"{BridgeHarness.ServerName}__set_watch_resource" }),
            ],
            resources: store.Resources(),
            configure: e => e.Subscriptions = true,
            configureBridge: o =>
            {
                o.WatchDebounceMs = 50;
                o.SubscriptionReconnectMinMs = 10;
                o.SubscriptionReconnectMaxMs = 200;
                o.SubscriptionAckTimeoutSeconds = 10;
                configureBridge?.Invoke(o);
            },
            timeProvider: timeProvider,
            configureServer: store.Configure);

    private static async Task<string> WatchAsync(BridgeHarness harness, string? onChange = null, string uri = "", string session = Session)
    {
        var response = await harness.WatchWithInstructionsAsync(uri.Length == 0 ? Poll : uri, session, onChange);
        Assert.IsNull(response.Error, response.Error);
        return response.WatchId!;
    }

    private static McpWatchEntry LedgerEntry(BridgeHarness harness, string watchId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(harness.WatchLedgerPath));
        return doc.RootElement.GetProperty("entries").Deserialize<McpWatchEntry[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Single(e => e.WatchId == watchId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    private static string SetArgs(string text) => JsonSerializer.Serialize(new { name = "poll", text });

    // ── A watch's own instructions ──────────────────────────────────────────

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatchsInstructions_ArriveWithEachChange_AndCanBeReplacedOrCleared()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);

        var response = await harness.WatchWithInstructionsAsync(Poll, Session, "When everyone has voted, finalize the best slot.");
        Assert.IsNull(response.Error, response.Error);
        StringAssert.Contains(response.Message, "> When everyone has voted, finalize the best slot.");
        var watchId = response.WatchId!;
        Assert.AreEqual("When everyone has voted, finalize the best slot.", LedgerEntry(harness, watchId).OnChange);

        store.Set("poll", "v2");
        var first = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v2");
        Assert.AreEqual("When everyone has voted, finalize the best slot.", first.OnChange);
        Assert.IsTrue(first.NeedsReply);

        // Asked again without on_change: the instructions stay.
        var again = await harness.WatchWithInstructionsAsync(Poll, Session, onChange: null);
        Assert.AreEqual(watchId, again.WatchId);
        Assert.AreEqual("When everyone has voted, finalize the best slot.", LedgerEntry(harness, watchId).OnChange);

        // Replaced.
        again = await harness.WatchWithInstructionsAsync(Poll, Session, "Only tell me; don't act.");
        StringAssert.Contains(again.Message, "replaced");
        store.Set("poll", "v3");
        var second = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v3");
        Assert.AreEqual("Only tell me; don't act.", second.OnChange);

        // Cleared.
        again = await harness.WatchWithInstructionsAsync(Poll, Session, "");
        StringAssert.Contains(again.Message, "cleared");
        store.Set("poll", "v4");
        var third = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v4");
        Assert.IsNull(third.OnChange);
        Assert.IsNull(LedgerEntry(harness, watchId).OnChange);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Instructions_AreRefused_UnlessTheUsersOwnMessageGaveThem()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);

        var refused = await harness.WatchWithInstructionsAsync(Poll, Session, "finalize it", instructionsAllowed: false);
        StringAssert.Contains(refused.Error, "only come from the user's own message");
        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count, "nothing was watched");

        // A watch already set up keeps its instructions against a turn that may not change them.
        var watchId = await WatchAsync(harness, "the user's own words");
        refused = await harness.WatchWithInstructionsAsync(Poll, Session, "words from a resource", instructionsAllowed: false);
        Assert.IsNotNull(refused.Error);
        Assert.AreEqual("the user's own words", LedgerEntry(harness, watchId).OnChange);

        // Without on_change, such a turn may still ask for the watch again.
        var kept = await harness.WatchWithInstructionsAsync(Poll, Session, onChange: null, instructionsAllowed: false);
        Assert.IsNull(kept.Error, kept.Error);

        var tooLong = await harness.WatchWithInstructionsAsync(Poll, Session, new string('x', McpManagementExecutor.MaxWatchInstructionsChars + 1));
        StringAssert.Contains(tooLong.Error, "the limit is");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnEnding_QuotesTheInstructions_SoTheyCanBeGivenAgain()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        var watchId = await WatchAsync(harness, "Propose new times if someone rules out every slot.");

        store.Delete("poll");
        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(watchId, ended.TaskId);
        StringAssert.Contains(ended.Content, "> Propose new times if someone rules out every slot.");
        Assert.AreEqual("Propose new times if someone rules out every slot.", ended.OnChange);
    }

    // ── Standing watch rules ────────────────────────────────────────────────

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AStandingRule_ArrivesWithEveryMatchingWatchsChange_InEveryConversation()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        store.Set("notes", "n1");
        await using var harness = await StartAsync(store);

        var set = await harness.SetWatchRuleAsync("fixture://watch/p*", "Check my calendar and propose new times.");
        Assert.IsNull(set.Error, set.Error);
        StringAssert.StartsWith(set.WatchId, "r_");
        var ruleId = set.WatchId!;

        await WatchAsync(harness);
        await WatchAsync(harness, uri: Poll, session: Other);
        await WatchAsync(harness, uri: WatchableResources.UriFor("notes"));

        store.Set("poll", "v2");
        await WaitUntilAsync(() => harness.TaskResults.Count(r => r.Kind == McpTaskResult.KindResourceUpdated) == 2, "both watchers' updates");
        foreach (var update in harness.TaskResults.Where(r => r.Kind == McpTaskResult.KindResourceUpdated))
        {
            var rule = update.WatchRules!.Single();
            Assert.AreEqual(ruleId, rule.RuleId);
            Assert.AreEqual("Check my calendar and propose new times.", rule.Instructions);
        }

        store.Set("notes", "n2");
        var notes = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "n2");
        Assert.IsNull(notes.WatchRules, "a URI the pattern doesn't match gets no rule");

        // Replacing keeps the rule's id; an empty on_change removes it.
        var replaced = await harness.SetWatchRuleAsync("fixture://watch/p*", "Just tell me.");
        Assert.AreEqual(ruleId, replaced.WatchId);
        StringAssert.Contains(replaced.Message, "Replaced");
        var removed = await harness.SetWatchRuleAsync("fixture://watch/p*", "");
        Assert.IsNull(removed.Error, removed.Error);
        store.Set("poll", "v3");
        var after = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v3");
        Assert.IsNull(after.WatchRules, "a removed rule no longer applies");

        var none = await harness.SetWatchRuleAsync("fixture://watch/p*", "");
        StringAssert.Contains(none.Error, "no standing watch rule");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AStandingRule_IsTheUsers_AndOnlyForAConfiguredServer()
    {
        var store = new WatchableResources();
        await using var harness = await StartAsync(store);

        var refused = await harness.SetWatchRuleAsync("fixture://watch/*", "act on it", instructionsAllowed: false);
        StringAssert.Contains(refused.Error, "only come from the user's own message");

        var unknown = await harness.SetWatchRuleAsync("x://*", "act on it", server: "nowhere");
        Assert.IsNotNull(unknown.Error);

        Assert.AreEqual(0, (await harness.Bridge.WatchRules.ActiveAsync()).Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARule_CanBeRemoved_AfterItsServerIsGone_ButNotSetForOne()
    {
        var store = new WatchableResources();
        await using var harness = await StartAsync(store);
        // A rule set while "retired" was configured; that server has since been removed.
        await harness.Bridge.WatchRules.SetAsync("retired", "retired://events/*/status", "Propose new times.");

        var setForIt = await harness.SetWatchRuleAsync("retired://events/*/status", "Something else.", server: "retired");
        Assert.IsNotNull(setForIt.Error, "a new rule needs a configured server");

        var removed = await harness.SetWatchRuleAsync("retired://events/*/status", "", server: "Retired");
        Assert.IsNull(removed.Error, removed.Error);
        Assert.AreEqual(0, (await harness.Bridge.WatchRules.ActiveAsync()).Count, "it no longer counts against the limit");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatchPastItsLifetime_AskedForAgain_IsWatchedAnew_NotReportedAsKept()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, timeProvider: clock, configureBridge: o =>
        {
            o.WatchTtlDays = 30;
            o.WatchMaxLifetimeDays = 1;
            o.WatchExpiryWarningHours = 0;
            o.WatchDebounceMs = 0;
        });
        var first = await WatchAsync(harness, "Propose new times.");

        // Past its lifetime: whether the expiry check or the request gets there first, the answer is
        // a new watch, never a kept one that has already ended.
        clock.Advance(TimeSpan.FromDays(2));
        var again = await harness.WatchWithInstructionsAsync(Poll, Session, onChange: null);
        Assert.IsNull(again.Error, again.Error);
        Assert.AreNotEqual(first, again.WatchId);
        StringAssert.StartsWith(again.Message, "[rockbot] Watching");
        await WaitUntilAsync(() => harness.Bridge.ActiveWatchEntries.Count == 1
                                   && harness.Bridge.ActiveWatchEntries[0].WatchId == again.WatchId, "only the new watch to be active");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheListing_ShowsEachWatchsInstructions_AndTheRules()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        store.Set("notes", "n1");
        await using var harness = await StartAsync(store);
        var ruleId = (await harness.SetWatchRuleAsync("fixture://watch/poll", "Propose new times.")).WatchId!;
        var pollWatch = await WatchAsync(harness, "Finalize when everyone voted.");
        var notesWatch = await WatchAsync(harness, uri: WatchableResources.UriFor("notes"));

        var listing = (await harness.ListWatchesAsync(Session)).Message!;
        StringAssert.Contains(listing, pollWatch);
        StringAssert.Contains(listing, notesWatch);
        StringAssert.Contains(listing, "> Finalize when everyone voted.");
        StringAssert.Contains(listing, "> Propose new times.");
        StringAssert.Contains(listing, ruleId);
        StringAssert.Contains(listing, "No instructions: a change is reported");

        var elsewhere = (await harness.ListWatchesAsync(Other)).Message!;
        StringAssert.Contains(elsewhere, "watches no resources");
        StringAssert.Contains(elsewhere, ruleId, "rules are the agent's, in every conversation");
    }

    // ── Lifetime ────────────────────────────────────────────────────────────

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatch_IsAnnouncedBeforeItsMaximumLifetime_AndAskingAgainKeepsIt()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, timeProvider: clock, configureBridge: o =>
        {
            o.WatchTtlDays = 30;
            o.WatchMaxLifetimeDays = 3;
            o.WatchExpiryWarningHours = 24;
            o.WatchDebounceMs = 0;
        });
        var watchId = await WatchAsync(harness, "Propose new times.");

        clock.Advance(TimeSpan.FromHours(60));
        var warning = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchExpiring);
        Assert.AreEqual(watchId, warning.TaskId);
        Assert.IsTrue(warning.NeedsReply);
        StringAssert.Contains(warning.Content, "maximum lifetime");
        StringAssert.Contains(warning.Content, "> Propose new times.");
        await WaitUntilAsync(() => LedgerEntry(harness, watchId).ExpiryWarnedAt is not null, "the warning to be recorded");

        // Asked again: its lifetime starts over, its instructions stay.
        var again = await harness.WatchWithInstructionsAsync(Poll, Session, onChange: null, instructionsAllowed: false);
        Assert.IsNull(again.Error, again.Error);
        var entry = LedgerEntry(harness, watchId);
        Assert.IsNull(entry.ExpiryWarnedAt);
        Assert.AreEqual(clock.GetUtcNow(), entry.LifetimeStartedAt!.Value);
        Assert.AreEqual("Propose new times.", entry.OnChange);

        // Past the old end, before the new one: warned again, not ended.
        clock.Advance(TimeSpan.FromHours(50));
        await WaitUntilAsync(() => harness.TaskResults.Count(r => r.Kind == McpTaskResult.KindWatchExpiring) == 2, "the second warning");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindWatchEnded), "kept past its first lifetime");
        var warnings = harness.TaskResults.Where(r => r.Kind == McpTaskResult.KindWatchExpiring).ToList();
        Assert.AreNotEqual(warnings[0].ContentHash, warnings[1].ContentHash, "each lifetime's warning is handled on its own");
        Assert.AreEqual(1, harness.Bridge.ActiveWatchEntries.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ASubagentsExpiryWarning_IsKeptUntilItAsksForTheWatchAgain()
    {
        // #854: a running subagent reads the warning from working memory; once kept, it no longer holds.
        const string subagent = "subagent/task-1";
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        var workingMemory = new McpHandbackEndToEndTests.RecordingWorkingMemory();
        await using var harness = await StartAsync(store, timeProvider: clock, workingMemory: workingMemory, configureBridge: o =>
        {
            o.WatchTtlDays = 30;
            o.WatchMaxLifetimeDays = 3;
            o.WatchExpiryWarningHours = 24;
            o.WatchDebounceMs = 0;
        });
        var watchId = await WatchAsync(harness, session: subagent);

        clock.Advance(TimeSpan.FromHours(60));
        var warning = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchExpiring);
        var key = $"{subagent}/mcp-watch/{BridgeHarness.ServerName}/{watchId}/expiring";
        Assert.AreEqual(key, warning.WorkingMemoryKey);
        StringAssert.Contains(workingMemory.Store[key], "maximum lifetime");

        var again = await harness.WatchWithInstructionsAsync(Poll, subagent, onChange: null, instructionsAllowed: false);
        Assert.IsNull(again.Error, again.Error);
        Assert.IsFalse(workingMemory.Store.ContainsKey(key), "the kept watch no longer reads as ending");
    }

    // ── Echoes of the session's own calls ───────────────────────────────────

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AChangeTheSessionsOwnCallMade_IsRecordedWithoutAReply_ButOthersStillHearOfIt()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        // No grace after the call, so a later change by someone else is told apart in a test's time.
        await using var harness = await StartAsync(store, configureBridge: o => o.WatchEchoGraceSeconds = 0);
        await WatchAsync(harness);
        await WatchAsync(harness, session: Other);

        var call = await harness.InvokeAsync("set_watch_resource", SetArgs("v2"), sessionId: Session);
        Assert.IsFalse(call.IsError, call.Content);

        await WaitUntilAsync(() => harness.TaskResults.Count(r => r.Kind == McpTaskResult.KindResourceUpdated) == 2, "both watchers' updates");
        var mine = harness.TaskResults.Single(r => r.Kind == McpTaskResult.KindResourceUpdated && r.SessionId == Session);
        var theirs = harness.TaskResults.Single(r => r.Kind == McpTaskResult.KindResourceUpdated && r.SessionId == Other);
        Assert.IsFalse(mine.NeedsReply, "the session made this change itself");
        Assert.AreEqual("set_watch_resource", mine.CausedByTool);
        Assert.IsTrue(theirs.NeedsReply, "another conversation didn't make it");
        Assert.IsNull(theirs.CausedByTool);

        // Someone else's change, later, is an ordinary update.
        await Task.Delay(300);
        store.Set("poll", "v3");
        var later = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.SessionId == Session && r.Content == "v3");
        Assert.IsTrue(later.NeedsReply);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AReadOnlyCall_IsNoEchosCause()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await WatchAsync(harness);

        var call = await harness.InvokeAsync("peek_watch_resource", SetArgs("v2"), sessionId: Session);
        Assert.IsFalse(call.IsError, call.Content);
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.IsTrue(update.NeedsReply);
        Assert.IsNull(update.CausedByTool);
    }

    private const string Aggregator = "aggregator";

    /// <summary>Registers the fixture again as <see cref="Aggregator"/>, at a URL the bridge doesn't take for a duplicate.</summary>
    private static async Task AddAggregatorAsync(BridgeHarness harness)
    {
        var registered = await harness.RegisterAsync(Aggregator, harness.ServerUrl + "?via=aggregator");
        Assert.IsTrue(registered.Success, registered.Error);
    }

    private static string InvokeToolArgs(string serverName, string text) => JsonSerializer.Serialize(new
    {
        serverName,
        toolName = "set_watch_resource",
        arguments = new { name = "poll", text },
    });

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AChangeMadeThroughAnAggregator_IsTheSessionsOwnEcho()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await AddAggregatorAsync(harness);
        await WatchAsync(harness);

        var call = await harness.InvokeAsync("invoke_tool", InvokeToolArgs(BridgeHarness.ServerName, "v2"),
            server: Aggregator, sessionId: Session);
        Assert.IsFalse(call.IsError, call.Content);

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.IsFalse(update.NeedsReply, "the session made this change itself, through the aggregator");
        Assert.AreEqual("set_watch_resource", update.CausedByTool);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AChangeMadeThroughAnAggregatorsTypedTool_IsTheSessionsOwnEcho()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await AddAggregatorAsync(harness);
        await WatchAsync(harness);

        var call = await harness.InvokeAsync($"{BridgeHarness.ServerName}__set_watch_resource", SetArgs("v2"),
            server: Aggregator, sessionId: Session);
        Assert.IsFalse(call.IsError, call.Content);

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.IsFalse(update.NeedsReply, "the session made this change itself, through the aggregator");
        Assert.AreEqual("set_watch_resource", update.CausedByTool);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnAggregatorCallToAServerTheBridgeLacks_IsNoEchosCause()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await AddAggregatorAsync(harness);
        await WatchAsync(harness);

        var call = await harness.InvokeAsync("invoke_tool", InvokeToolArgs("elsewhere", "v2"),
            server: Aggregator, sessionId: Session);
        Assert.IsFalse(call.IsError, call.Content);

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.IsTrue(update.NeedsReply, "no configured server of that name, so not the watched one");
        Assert.IsNull(update.CausedByTool);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task EchoSuppression_CanBeTurnedOff()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, configureBridge: o => o.WatchEchoSuppression = false);
        await WatchAsync(harness);

        await harness.InvokeAsync("set_watch_resource", SetArgs("v2"), sessionId: Session);
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.IsTrue(update.NeedsReply);
        Assert.IsNull(update.CausedByTool);
    }
}
