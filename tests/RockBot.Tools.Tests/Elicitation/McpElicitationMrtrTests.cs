using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

/// <summary>
/// End-to-end: a real MCP C# SDK 2.x server and client over in-memory streams, with the bridge's
/// <see cref="McpElicitationCoordinator"/> as the client's elicitation handler. Pins that #593's
/// coordinator answers Multi Round-Trip Request (MRTR) input requests — the 2026-07-28 protocol's
/// replacement for <c>elicitation/create</c> — and still answers legacy elicitation from servers
/// that negotiate an older protocol version.
/// </summary>
[TestClass]
public class McpElicitationMrtrTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static ElicitRequestParams MailboxQuestion() => new()
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
    };

    private static string Describe(ElicitResult result)
        => result.Action + ":" + (result.Content?.TryGetValue("mailbox", out var mailbox) == true ? mailbox.GetString() : "");

    /// <summary>
    /// MRTR tool: asks for the mailbox up to <paramref name="rounds"/> times (round number in
    /// requestState), stops at the first answer that is not an accept.
    /// </summary>
    private static McpServerTool MrtrTool(int rounds = 1) => McpServerTool.Create(
        (McpServer server, RequestContext<CallToolRequestParams> context) =>
        {
            if (!server.IsMrtrSupported)
                return "no-mrtr";

            var round = context.Params?.RequestState is { } state ? int.Parse(state) : 0;
            if (round > 0)
            {
                var response = context.Params!.InputResponses!["mailbox"];
                var answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                if (answer.Action != McpElicitationActions.Accept || round >= rounds)
                    return $"round {round} {Describe(answer)}";
            }

            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { ["mailbox"] = InputRequest.ForElicitation(MailboxQuestion()) },
                (round + 1).ToString());
        },
        new McpServerToolCreateOptions { Name = "search_mail" });

    /// <summary>Legacy-style tool: blocks on a server-to-client elicitation request.</summary>
    private static McpServerTool LegacyTool() => McpServerTool.Create(
        async (McpServer server, CancellationToken ct) =>
        {
            var answer = await server.ElicitAsync(MailboxQuestion(), ct);
            return Describe(answer);
        },
        new McpServerToolCreateOptions { Name = "search_mail" });

    private sealed class CountingResponder(string mailbox) : IMcpElicitationResponder
    {
        public int Calls { get; private set; }

        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(McpElicitationAnswer.Accept(
                new Dictionary<string, JsonElement> { ["mailbox"] = Json($"\"{mailbox}\"") }));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private McpServer _server = null!;
        private Task _serverRun = Task.CompletedTask;

        public McpClient Client { get; private set; } = null!;
        public McpElicitationCoordinator Coordinator { get; private set; } = null!;

        public static async Task<Harness> StartAsync(
            McpServerTool tool,
            McpElicitationConfig config,
            IMcpElicitationResponder? responder = null,
            string? serverProtocolVersion = null)
        {
            var harness = new Harness();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            harness._server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "fixture", NullLoggerFactory.Instance),
                new McpServerOptions { ToolCollection = [tool], ProtocolVersion = serverProtocolVersion },
                NullLoggerFactory.Instance);
            harness._serverRun = harness._server.RunAsync(harness._cts.Token);

            harness.Coordinator = McpElicitationCoordinator.TryCreate("mail", config, responder, NullLogger.Instance)!;
            var coordinator = harness.Coordinator;
            harness.Client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    Handlers = new McpClientHandlers
                    {
                        ElicitationHandler = (request, ct) => coordinator.HandleAsync(request, ct),
                    },
                },
                NullLoggerFactory.Instance,
                harness._cts.Token);

            return harness;
        }

        public async Task<string> CallAsync(string sessionId = "session/abc", IMcpHandbackChannel? handback = null)
        {
            using var scope = Coordinator.BeginCall("search_mail", "{}", sessionId, handback);
            var result = await Client.CallToolAsync("search_mail", new Dictionary<string, object?>(), cancellationToken: _cts.Token);
            LastRecords = scope.Records;
            return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        }

        public IReadOnlyList<McpElicitationRecord> LastRecords { get; private set; } = [];

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _cts.Cancel();
            try { await _serverRun; } catch (OperationCanceledException) { }
            await _server.DisposeAsync();
            _cts.Dispose();
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Mrtr_TheCoordinatorAnswersAnInputRequest()
    {
        var config = new McpElicitationConfig { Defaults = new() { ["mailbox"] = Json("\"work\"") } };
        await using var harness = await Harness.StartAsync(MrtrTool(), config);

        var text = await harness.CallAsync();

        Assert.AreEqual("round 1 accept:work", text, "the SDK must negotiate MRTR and deliver the coordinator's answer on the retry");
        var record = harness.LastRecords.Single();
        Assert.AreEqual(McpElicitationActions.Accept, record.Action);
        CollectionAssert.AreEqual(new[] { "mailbox" }, record.AnsweredFields.ToArray());
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Mrtr_ADeclineReachesTheServer()
    {
        // No defaults and no responder: the coordinator can only decline.
        await using var harness = await Harness.StartAsync(MrtrTool(), new McpElicitationConfig());

        var text = await harness.CallAsync();

        Assert.AreEqual("round 1 decline:", text);
        Assert.AreEqual(McpElicitationActions.Decline, harness.LastRecords.Single().Action);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Mrtr_MaxPerCallStillBoundsRoundsTheSdkDrives()
    {
        // The server keeps asking for five rounds; the SDK's retry loop would oblige forever.
        var responder = new CountingResponder("work");
        await using var harness = await Harness.StartAsync(
            MrtrTool(rounds: 5), new McpElicitationConfig { MaxPerCall = 2 }, responder);

        var text = await harness.CallAsync();

        Assert.AreEqual(2, responder.Calls, "rounds past MaxPerCall must be declined without consulting the responder");
        Assert.AreEqual("round 3 decline:", text);
        Assert.AreEqual(3, harness.LastRecords.Count);
    }

    /// <summary>Answers only once <paramref name="expected"/> questions are waiting at the same time.</summary>
    private sealed class RendezvousResponder(int expected) : IMcpElicitationResponder
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public List<int> InFlightCounts { get; } = [];

        public async ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
        {
            lock (InFlightCounts) InFlightCounts.Add(context.InFlightCalls.Count);
            if (Interlocked.Increment(ref _arrived) == expected)
                _allArrived.SetResult();
            await _allArrived.Task.WaitAsync(ct);
            return McpElicitationAnswer.Accept(new Dictionary<string, JsonElement> { ["mailbox"] = Json($"\"{context.InFlightCalls[0].SessionId![^4..]}\"") });
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Mrtr_ConcurrentCallsAreAttributedExactly()
    {
        // Two calls against the same server, both open while both questions are being answered.
        // Under MRTR each question is resolved in its own call's async flow, so each call gets
        // its own record and the responder sees only its own call — not "several calls are open".
        var responder = new RendezvousResponder(expected: 2);
        var tool = McpServerTool.Create(
            (McpServer server, RequestContext<CallToolRequestParams> context) =>
            {
                if (context.Params?.RequestState is not null)
                {
                    var answer = context.Params.InputResponses!["mailbox"].Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                    return Describe(answer);
                }

                var question = MailboxQuestion();
                ((ElicitRequestParams.UntitledSingleSelectEnumSchema)question.RequestedSchema!.Properties["mailbox"]).Enum = ["work", "home"];
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest> { ["mailbox"] = InputRequest.ForElicitation(question) }, "1");
            },
            new McpServerToolCreateOptions { Name = "search_mail" });

        await using var harness = await Harness.StartAsync(tool, new McpElicitationConfig(), responder);

        async Task<(string Text, IReadOnlyList<McpElicitationRecord> Records)> CallAs(string session)
        {
            using var scope = harness.Coordinator.BeginCall("search_mail", "{}", session);
            var result = await harness.Client.CallToolAsync("search_mail", new Dictionary<string, object?>());
            return (string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)), scope.Records);
        }

        var calls = await Task.WhenAll(CallAs("session/work"), CallAs("session/home"));

        CollectionAssert.AreEqual(new[] { 1, 1 }, responder.InFlightCounts, "each question must see only the call that asked it");
        Assert.AreEqual("accept:work", calls[0].Text);
        Assert.AreEqual("accept:home", calls[1].Text);
        Assert.AreEqual(1, calls[0].Records.Count, "a question must not be recorded against the other call");
        Assert.AreEqual(1, calls[1].Records.Count);
    }

    // ── Hand-back (#602) ─────────────────────────────────────────────────────

    /// <summary>Stands in for the bridge's hand-back: answers each question it is given in turn.</summary>
    private sealed class ScriptedChannel(params McpHandbackOutcome[] outcomes) : IMcpHandbackChannel
    {
        public List<McpHandbackQuestion> Asked { get; } = [];

        public ValueTask<McpHandbackOutcome> AskAsync(McpHandbackQuestion question, CancellationToken ct)
        {
            Asked.Add(question);
            return ValueTask.FromResult(outcomes[Math.Min(Asked.Count, outcomes.Length) - 1]);
        }
    }

    private static McpHandbackOutcome AcceptMailbox(string mailbox) =>
        McpHandbackOutcome.Accept(new Dictionary<string, JsonElement> { ["mailbox"] = Json($"\"{mailbox}\"") });

    private static McpElicitationConfig Handback() => new() { Mode = McpElicitationConfig.ModeHandback };

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_TheAgentsAnswerResumesTheCall()
    {
        var responder = new CountingResponder("personal");
        var channel = new ScriptedChannel(AcceptMailbox("work"));
        await using var harness = await Harness.StartAsync(MrtrTool(), Handback(), responder);

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual("round 1 accept:work", text);
        Assert.AreEqual(0, responder.Calls, "in hand-back mode the responder is only the fallback");
        Assert.AreEqual("mail", channel.Asked.Single().ServerName);
        Assert.AreEqual(1, channel.Asked.Single().Round);
        Assert.AreEqual("answered by the calling agent", harness.LastRecords.Single().Reason);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_EachRoundIsHandedBack()
    {
        var channel = new ScriptedChannel(AcceptMailbox("work"), AcceptMailbox("personal"));
        await using var harness = await Harness.StartAsync(MrtrTool(rounds: 2), Handback());

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual("round 2 accept:personal", text);
        CollectionAssert.AreEqual(new[] { 1, 2 }, channel.Asked.Select(q => q.Round).ToArray());
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_MaxPerCallStillBoundsTheRounds()
    {
        var channel = new ScriptedChannel(AcceptMailbox("work"));
        await using var harness = await Harness.StartAsync(
            MrtrTool(rounds: 5), new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback, MaxPerCall = 2 });

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual(2, channel.Asked.Count);
        Assert.AreEqual("round 3 decline:", text);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_TheAgentsDeclineReachesTheServer()
    {
        var channel = new ScriptedChannel(McpHandbackOutcome.Decline("the user would rather not say"));
        await using var harness = await Harness.StartAsync(MrtrTool(), Handback());

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual("round 1 decline:", text);
        Assert.AreEqual("the user would rather not say", harness.LastRecords.Single().Reason);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_AnAnswerOutsideTheFormIsDeclinedNotForwarded()
    {
        var channel = new ScriptedChannel(AcceptMailbox("shared"));
        await using var harness = await Harness.StartAsync(MrtrTool(), Handback());

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual("round 1 decline:", text, "a value the form doesn't allow must never reach the server");
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_InBandChecksStillRunFirst()
    {
        // A denied field declines the whole form before anyone is asked.
        var denied = new ScriptedChannel(AcceptMailbox("work"));
        await using (var harness = await Harness.StartAsync(MrtrTool(),
                         new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback, DeniedFields = ["mailbox"] }))
        {
            Assert.AreEqual("round 1 decline:", await harness.CallAsync(handback: denied));
        }
        Assert.AreEqual(0, denied.Asked.Count);

        // Operator defaults that settle the whole form answer it without a hand-back.
        var defaulted = new ScriptedChannel(AcceptMailbox("work"));
        await using (var harness = await Harness.StartAsync(MrtrTool(), new McpElicitationConfig
                     {
                         Mode = McpElicitationConfig.ModeHandback,
                         Defaults = new() { ["mailbox"] = Json("\"personal\"") },
                     }))
        {
            Assert.AreEqual("round 1 accept:personal", await harness.CallAsync(handback: defaulted));
        }
        Assert.AreEqual(0, defaulted.Asked.Count);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_ACallThatCantTakeOneGetsTheResponder()
    {
        // No channel on the scope: the caller can't answer (a wisp, a subagent), so the server's
        // responder answers, as in auto mode.
        var responder = new CountingResponder("personal");
        await using var harness = await Harness.StartAsync(MrtrTool(), Handback(), responder);

        var text = await harness.CallAsync();

        Assert.AreEqual("round 1 accept:personal", text);
        Assert.AreEqual(1, responder.Calls);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_ALegacyElicitationGetsTheResponder()
    {
        // A legacy elicitation/create arrives off the call's async flow, so it can't be tied to
        // exactly one call, and it holds the server's request open while it waits: never handed back.
        var responder = new CountingResponder("personal");
        var channel = new ScriptedChannel(AcceptMailbox("work"));
        await using var harness = await Harness.StartAsync(LegacyTool(), Handback(), responder, serverProtocolVersion: "2025-11-25");

        var text = await harness.CallAsync(handback: channel);

        Assert.AreEqual("accept:personal", text);
        Assert.AreEqual(0, channel.Asked.Count);
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Handback_ADecisionIsHandedBackRatherThanDeclined()
    {
        // In auto mode a yes/no field is declined unless a default settles it; in hand-back mode
        // it goes to the agent, whose answer the bridge accepts only after the user has spoken.
        var tool = McpServerTool.Create(
            (McpServer server, RequestContext<CallToolRequestParams> context) =>
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
                    }, "1");
            },
            new McpServerToolCreateOptions { Name = "search_mail" });

        var channel = new ScriptedChannel(McpHandbackOutcome.Accept(new Dictionary<string, JsonElement> { ["confirm"] = Json("true") }));
        await using var harness = await Harness.StartAsync(tool, Handback());

        Assert.AreEqual("accept:true", await harness.CallAsync(handback: channel));
        Assert.AreEqual(1, channel.Asked.Count);
    }

    [TestMethod]
    public void Handback_IsRecognizedAndRefusedAsTheBridgeWideDefault()
    {
        var config = new McpElicitationConfig { Mode = "HandBack", Responder = "conversation", MaxPerCall = 4 };

        Assert.IsTrue(config.IsRecognizedMode);
        Assert.AreEqual(McpElicitationConfig.ModeHandback, config.ResolveMode());

        var asDefault = config.WithoutHandback();
        Assert.AreEqual(McpElicitationConfig.ModeAuto, asDefault.ResolveMode());
        Assert.AreEqual(4, asDefault.MaxPerCall, "only the mode changes");
        Assert.AreEqual("conversation", asDefault.Responder);

        var auto = new McpElicitationConfig();
        Assert.AreSame(auto, auto.WithoutHandback());
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task OlderProtocol_LegacyElicitationIsStillAnswered()
    {
        // A server that only speaks an initialize-handshake protocol version — how an external
        // server still on SDK 1.x looks to the 2.x client — elicits with a server-to-client
        // request. The client falls back automatically and the same handler answers it.
        var config = new McpElicitationConfig { Defaults = new() { ["mailbox"] = Json("\"personal\"") } };
        await using var harness = await Harness.StartAsync(LegacyTool(), config, serverProtocolVersion: "2025-11-25");

        var text = await harness.CallAsync();

        Assert.AreEqual("accept:personal", text);
        Assert.AreEqual(McpElicitationActions.Accept, harness.LastRecords.Single().Action);
    }
}
