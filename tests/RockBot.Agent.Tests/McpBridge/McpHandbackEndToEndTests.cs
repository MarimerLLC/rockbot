using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Handback;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Elicitation;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// MCP elicitation hand-back end to end (#602, <c>design/mcp-elicitation-handback.md</c>): a real
/// bridge, a real MRTR server over streamable HTTP, the question handed back as the tool result,
/// <c>mcp_answer</c> resuming the parked call, and the pending ledger across a restart.
/// </summary>
[TestClass]
public class McpHandbackEndToEndTests
{
    private const string Session = "session/test";

    private static readonly Regex QuestionId = new("q_[0-9a-f]{32}", RegexOptions.Compiled);

    /// <summary>MRTR tool: asks which mailbox, up to <paramref name="rounds"/> times.</summary>
    internal static McpServerTool MailboxTool(int rounds = 1) => McpServerTool.Create(
        (McpServer server, RequestContext<CallToolRequestParams> context) =>
        {
            if (!server.IsMrtrSupported)
                return "no-mrtr";

            var round = context.Params?.RequestState is { } state ? int.Parse(state) : 0;
            if (round > 0)
            {
                var answer = context.Params!.InputResponses!["mailbox"].Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                var mailbox = answer.Content?.TryGetValue("mailbox", out var m) == true ? m.GetString() : "";
                if (answer.Action != McpElicitationActions.Accept || round >= rounds)
                    return $"round {round} {answer.Action}:{mailbox}";
            }

            throw new InputRequiredException(
                new Dictionary<string, InputRequest>
                {
                    ["mailbox"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = "Which mailbox should I search?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["mailbox"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["work", "personal"] },
                            },
                            Required = ["mailbox"],
                        },
                    }),
                },
                (round + 1).ToString());
        },
        new McpServerToolCreateOptions { Name = "search_mail", Description = "Search a mailbox." });

    /// <summary>MRTR tool whose question is a yes/no decision.</summary>
    private static McpServerTool ConfirmTool() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is not null)
            {
                var answer = context.Params.InputResponses!["confirm"].Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                return answer.Action + ":" + (answer.Content?.TryGetValue("confirm", out var c) == true ? c.GetRawText() : "");
            }

            throw new InputRequiredException(
                new Dictionary<string, InputRequest>
                {
                    ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = "Delete 12 rows?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["confirm"] = new ElicitRequestParams.BooleanSchema(),
                            },
                            Required = ["confirm"],
                        },
                    }),
                },
                "1");
        },
        new McpServerToolCreateOptions { Name = "delete_rows" });

    private static McpServerTool EchoTool() => McpServerTool.Create(
        (string text) => $"echo:{text}",
        new McpServerToolCreateOptions { Name = "echo" });

    private static Task<BridgeHarness> StartAsync(
        IEnumerable<McpServerTool>? tools = null,
        Action<McpBridgeOptions>? configureBridge = null,
        IConversationMemory? conversationMemory = null,
        IWorkingMemory? workingMemory = null,
        Action<string>? beforeBridgeStarts = null,
        TimeProvider? timeProvider = null) =>
        BridgeHarness.StartAsync(
            tools ?? [MailboxTool(), ConfirmTool(), EchoTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback },
            configureBridge: configureBridge,
            conversationMemory: conversationMemory,
            workingMemory: workingMemory,
            beforeBridgeStarts: beforeBridgeStarts,
            timeProvider: timeProvider);

    internal static string IdIn(string? content)
    {
        var match = QuestionId.Match(content ?? "");
        Assert.IsTrue(match.Success, $"Expected a question_id in: {content}");
        return match.Value;
    }

    private static List<PendingQuestionEntry> ReadLedger(BridgeHarness harness)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(harness.LedgerPath));
        Assert.AreEqual(PendingQuestionLedger.CurrentVersion, document.RootElement.GetProperty("version").GetInt32());
        return JsonSerializer.Deserialize<List<PendingQuestionEntry>>(
            document.RootElement.GetProperty("entries").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheQuestionIsHandedBack_AndTheAnswerResumesTheCall()
    {
        await using var harness = await StartAsync();

        var handedBack = await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true);

        Assert.IsFalse(handedBack.IsError);
        StringAssert.Contains(handedBack.Content, "needs input");
        StringAssert.Contains(handedBack.Content, "Which mailbox should I search?");
        StringAssert.Contains(handedBack.Content, "mcp_answer(question_id:");
        var id = IdIn(handedBack.Content);

        // Write-ahead: the entry was on disk before the agent saw the question.
        var entry = ReadLedger(harness).Single();
        Assert.AreEqual(id, entry.QuestionId);
        Assert.AreEqual(PendingQuestionStatus.Pending, entry.Status);
        Assert.AreEqual(Session, entry.SessionId);
        Assert.AreEqual("search_mail", entry.Call.Tool);
        Assert.AreEqual("Search a mailbox.", entry.Call.ToolDescription);
        Assert.AreEqual("mailbox", entry.Question.Fields.Single().Name);

        var answered = await harness.AnswerAsync(id, """{"mailbox":"work"}""");

        Assert.IsNull(answered.Error, answered.Error);
        StringAssert.StartsWith(answered.Result!.Content, "round 1 accept:work");
        Assert.AreEqual(BridgeHarness.ServerName, answered.ServerName);
        Assert.AreEqual("search_mail", answered.ToolName);
        Assert.AreEqual(PendingQuestionStatus.Answered, ReadLedger(harness).Single().Status);
        Assert.IsFalse(File.ReadAllText(harness.LedgerPath).Contains("\"work\""), "the ledger never holds answer values");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AParkedCallDoesNotHoldUpOtherCalls()
    {
        await using var harness = await StartAsync();

        var handedBack = await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true);
        IdIn(handedBack.Content);

        // The bridge takes tool calls one at a time; the parked call must already be off that path.
        var other = await harness.InvokeAsync("echo", """{"text":"hi"}""");

        Assert.IsFalse(other.IsError, other.Content);
        StringAssert.Contains(other.Content, "echo:hi");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ACallerThatCantAnswer_GetsTheServersResponderInstead()
    {
        await using var harness = await StartAsync();

        // No hand-back header (a wisp, a subagent): no responder is configured, so it's declined
        // in-band and the call finishes on the server's terms.
        var result = await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: false);

        StringAssert.StartsWith(result.Content, "round 1 decline:");
        Assert.IsFalse(File.Exists(harness.LedgerPath) && ReadLedger(harness).Count > 0);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AServersNextQuestionIsHandedBackToTheAnswer()
    {
        await using var harness = await StartAsync(tools: [MailboxTool(rounds: 2)]);

        var first = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);
        var second = await harness.AnswerAsync(first, """{"mailbox":"work"}""");

        Assert.IsNull(second.Error, second.Error);
        StringAssert.Contains(second.Result!.Content, "needs input");
        var secondId = IdIn(second.Result.Content);
        Assert.AreNotEqual(first, secondId);
        Assert.AreEqual(2, ReadLedger(harness).Single(e => e.QuestionId == secondId).Call.Round);
        CollectionAssert.AreEqual(new[] { "mailbox" },
            ReadLedger(harness).Single(e => e.QuestionId == secondId).Call.EarlierRounds.Single().AnsweredFields);

        var final = await harness.AnswerAsync(secondId, """{"mailbox":"personal"}""");

        StringAssert.StartsWith(final.Result!.Content, "round 2 accept:personal");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ADeclineReachesTheServer()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);

        var declined = await harness.AnswerAsync(id, answersJson: null, decline: true);

        StringAssert.StartsWith(declined.Result!.Content, "round 1 decline:");
        Assert.AreEqual(PendingQuestionStatus.Declined, ReadLedger(harness).Single().Status);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnotherSessionCantAnswer_AndCantTellTheQuestionExists()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);

        var stranger = await harness.AnswerAsync(id, """{"mailbox":"work"}""", sessionId: "session/other");
        var unknown = await harness.AnswerAsync("q_" + new string('0', 32), """{"mailbox":"work"}""");

        StringAssert.Contains(stranger.Error, "no open question");
        StringAssert.Contains(unknown.Error, "no open question");

        var owner = await harness.AnswerAsync(id, """{"mailbox":"work"}""");
        StringAssert.StartsWith(owner.Result!.Content, "round 1 accept:work", "a refused answer leaves the question open");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnAnswerOutsideTheForm_IsRefusedAndTheQuestionStaysOpen()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);

        var wrong = await harness.AnswerAsync(id, """{"mailbox":"shared"}""");
        var malformed = await harness.AnswerAsync(id, "\"work\"");

        StringAssert.Contains(wrong.Error, "doesn't fit");
        StringAssert.Contains(malformed.Error, "JSON object");

        var right = await harness.AnswerAsync(id, """{"Mailbox":"work"}""");
        StringAssert.StartsWith(right.Result!.Content, "round 1 accept:work", "field names match case-insensitively");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AQuestionIsAnsweredOnce()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);

        await harness.AnswerAsync(id, """{"mailbox":"work"}""");
        var again = await harness.AnswerAsync(id, """{"mailbox":"personal"}""");

        StringAssert.Contains(again.Error, "already answered");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ADecision_NeedsTheUserToHaveSpokenSinceTheHandBack()
    {
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(conversationMemory: conversation);
        var id = IdIn((await harness.InvokeAsync("delete_rows", "{}", sessionId: Session, canAnswer: true)).Content);

        var tooSoon = await harness.AnswerAsync(id, """{"confirm":true}""");
        StringAssert.Contains(tooSoon.Error, "decision");

        // An agent's synthetic turn doesn't count; the user's own does.
        await conversation.AddTurnAsync("test", new ConversationTurn("user", "[subagent result]", DateTimeOffset.UtcNow) { AgentName = "subagent-1" });
        StringAssert.Contains((await harness.AnswerAsync(id, """{"confirm":true}""")).Error, "decision");

        await conversation.AddTurnAsync("test", new ConversationTurn("user", "yes, delete them", DateTimeOffset.UtcNow));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""");

        Assert.IsNull(confirmed.Error, confirmed.Error);
        StringAssert.StartsWith(confirmed.Result!.Content, "accept:true");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ADecisionCanBeDeclinedWithoutTheUser()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("delete_rows", "{}", sessionId: Session, canAnswer: true)).Content);

        var declined = await harness.AnswerAsync(id, answersJson: null, decline: true);

        StringAssert.StartsWith(declined.Result!.Content, "decline:");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheTriggeringUserMessageIsRecorded_Scrubbed()
    {
        var conversation = new RecordingConversationMemory();
        await conversation.AddTurnAsync("test", new ConversationTurn("user",
            "Search my mail for the invoice; my token is sk-ant-abcdefghijklmnopqrstuvwxyz0123456789", DateTimeOffset.UtcNow.AddSeconds(-5)));
        await using var harness = await StartAsync(conversationMemory: conversation);

        IdIn((await harness.InvokeAsync("search_mail", """{"apiKey":"hunter2","q":"invoice"}""", sessionId: Session, canAnswer: true)).Content);

        var entry = ReadLedger(harness).Single();
        StringAssert.Contains(entry.TriggeredBy!.UserExcerpt, "Search my mail for the invoice");
        Assert.IsFalse(entry.TriggeredBy.UserExcerpt.Contains("sk-ant-abcdefghijklmnopqrstuvwxyz0123456789"));
        Assert.IsFalse(entry.Call.Arguments!.Contains("hunter2"), "credential-named arguments are redacted");
        StringAssert.Contains(entry.Call.Arguments, "invoice");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PastTheSessionCap_AQuestionIsDeclinedInBand()
    {
        await using var harness = await StartAsync(configureBridge: o => o.MaxPendingQuestionsPerSession = 1);

        IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);
        var second = await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true);
        var otherSession = await harness.InvokeAsync("search_mail", "{}", sessionId: "session/other", canAnswer: true);

        StringAssert.StartsWith(second.Content, "round 1 decline:");
        StringAssert.Contains(second.Content, "unanswered questions");
        IdIn(otherSession.Content);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnUnansweredQuestionExpires_AndALateAnswerIsToldSo()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await StartAsync(timeProvider: time);
        var id = IdIn((await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canAnswer: true)).Content);

        time.Advance(TimeSpan.FromMinutes(31));
        await WaitUntilAsync(() => ReadLedger(harness).Single().Status == PendingQuestionStatus.Expired);

        var late = await harness.AnswerAsync(id, """{"mailbox":"work"}""");
        StringAssert.Contains(late.Error, "expired");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARestartInterruptsTheCall_AndTheSessionIsTold()
    {
        var conversation = new RecordingConversationMemory();
        var working = new RecordingWorkingMemory();
        await using var harness = await StartAsync(conversationMemory: conversation, workingMemory: working);
        var id = IdIn((await harness.InvokeAsync("search_mail", """{"q":"invoice"}""", sessionId: Session, canAnswer: true)).Content);

        await harness.RestartBridgeAsync();

        var notice = await harness.WaitForAsync("user.response.test-agent", TimeSpan.FromSeconds(10));
        var reply = notice.GetPayload<AgentReply>()!;
        Assert.AreEqual("test", reply.SessionId);
        StringAssert.Contains(reply.Content, "interrupted by a restart");
        StringAssert.Contains(reply.Content, "search_mail");
        StringAssert.Contains(reply.Content, "Which mailbox should I search?");

        await WaitUntilAsync(() => ReadLedger(harness).Single().Status == PendingQuestionStatus.Notified);
        Assert.IsTrue(working.Store.ContainsKey($"{Session}/mcp-interrupted/{id}"));
        var turn = conversation.Turns("test").Last();
        Assert.AreEqual(McpBridgeService.RestartNoticeAgentName, turn.AgentName, "the notice must not pass for the user's own turn");

        var late = await harness.AnswerAsync(id, """{"mailbox":"work"}""");
        StringAssert.Contains(late.Error, "interrupted by a restart");

        // Told once: a second restart has nothing left to announce.
        await harness.RestartBridgeAsync();
        await Task.Delay(300);
        Assert.AreEqual(0, harness.PublishedOn("user.response.test-agent").Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AtStartup_AnExpiredPendingEntryIsNotAnnounced()
    {
        await using var harness = await StartAsync(beforeBridgeStarts: dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "mcp"));
            File.WriteAllText(Path.Combine(dir, "mcp", "pending-questions.json"), $$$"""
                {"version":1,"entries":[{"questionId":"q_old","status":"pending","sessionId":"{{{Session}}}",
                 "createdAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:30:00Z",
                 "call":{"server":"fixture","tool":"search_mail"},"question":{"message":"Which?"}}]}
                """);
        });

        await Task.Delay(300);

        Assert.AreEqual(PendingQuestionStatus.Expired, ReadLedger(harness).Single().Status);
        Assert.AreEqual(0, harness.PublishedOn("user.response.test-agent").Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met within 10 s.");
            await Task.Delay(25);
        }
    }

    private sealed class RecordingConversationMemory : IConversationMemory
    {
        private readonly ConcurrentDictionary<string, List<ConversationTurn>> _turns = new();

        public IReadOnlyList<ConversationTurn> Turns(string sessionId)
        {
            var list = _turns.GetOrAdd(sessionId, _ => []);
            lock (list) return [.. list];
        }

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
        {
            var list = _turns.GetOrAdd(sessionId, _ => []);
            lock (list) list.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Turns(sessionId));

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _turns.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. _turns.Keys]);
    }

    private sealed class RecordingWorkingMemory : IWorkingMemory
    {
        public ConcurrentDictionary<string, string> Store { get; } = new();

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult(Store.GetValueOrDefault(key));

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key)
        {
            Store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            Store.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}

/// <summary>A clock tests move by hand. Timers fire when <see cref="Advance"/> passes their due time.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
            foreach (var timer in due)
                timer.Due = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            _timers.Add(timer);
        }
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
