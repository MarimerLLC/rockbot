using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class McpElicitationCoordinatorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static ElicitRequestParams FormRequest(
        params (string Name, ElicitRequestParams.PrimitiveSchemaDefinition Definition)[] fields)
        => FormRequest("Which mailbox should I search?", fields);

    private static ElicitRequestParams FormRequest(
        string message,
        params (string Name, ElicitRequestParams.PrimitiveSchemaDefinition Definition)[] fields)
    {
        var request = new ElicitRequestParams { Message = message };
        if (fields.Length == 0)
            return request;

        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var (name, definition) in fields)
            properties[name] = definition;

        request.RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = properties,
            Required = [.. properties.Keys],
        };

        return request;
    }

    private static McpElicitationCoordinator Create(
        McpElicitationConfig? config = null,
        IMcpElicitationResponder? responder = null)
    {
        var coordinator = McpElicitationCoordinator.TryCreate("mail", config, responder, NullLogger.Instance);
        Assert.IsNotNull(coordinator);
        return coordinator!;
    }

    private sealed class StubResponder(Func<McpElicitationContext, McpElicitationAnswer> answer)
        : IMcpElicitationResponder
    {
        public int Calls { get; private set; }

        public McpElicitationContext? LastContext { get; private set; }

        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
        {
            Calls++;
            LastContext = context;
            return ValueTask.FromResult(answer(context));
        }
    }

    [TestMethod]
    public void TryCreate_ReturnsNull_WhenModeIsOff()
    {
        var coordinator = McpElicitationCoordinator.TryCreate(
            "mail", new McpElicitationConfig { Mode = McpElicitationConfig.ModeOff }, null, NullLogger.Instance);

        Assert.IsNull(coordinator, "an 'off' server must not advertise the elicitation capability");
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenNoToolCallIsInFlight()
    {
        var coordinator = Create();

        var result = await coordinator.HandleAsync(FormRequest(), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenModeIsDecline()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>()));
        var coordinator = Create(new McpElicitationConfig { Mode = McpElicitationConfig.ModeDecline }, responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(FormRequest(), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenModeIsNotRecognized()
    {
        // A responder ready to accept and a field it could answer: only the mode can decline this.
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(
            new Dictionary<string, JsonElement> { ["mailbox"] = Json("\"work\"") }));
        var coordinator = Create(new McpElicitationConfig { Mode = "yolo" }, responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls, "an unrecognized mode must fail closed, not reach the responder");
    }

    [TestMethod]
    public async Task HandleAsync_Declines_UrlModeBecauseThereIsNoBrowser()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>()));
        var coordinator = Create(responder: responder);

        var request = new ElicitRequestParams
        {
            Message = "Finish signing in",
            Mode = "url",
            Url = "https://example.test/consent",
            ElicitationId = "e1",
        };

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(request, CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenTheFormAsksForACredential()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>
        {
            ["apiKey"] = Json("\"sk-not-a-real-key\""),
        }));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest("I need your key", ("apiKey", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls, "a credential request must never reach the responder");
    }

    [TestMethod]
    public async Task HandleAsync_Declines_AYesNoConfirmationPrompt()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>
        {
            ["confirm"] = Json("true"),
        }));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("delete_rows", "{\"table\":\"invoices\"}");
        var result = await coordinator.HandleAsync(
            FormRequest("This overwrites 12 rows. Continue?", ("confirm", new ElicitRequestParams.BooleanSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls, "a confirmation is a decision, not a transcription");
    }

    [TestMethod]
    public async Task HandleAsync_AnswersAConfirmationTheOperatorPreAnswered()
    {
        var config = new McpElicitationConfig
        {
            Defaults = { ["confirm"] = Json("true") },
        };
        var coordinator = Create(config);

        using var scope = coordinator.BeginCall("delete_rows", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest("Continue?", ("confirm", new ElicitRequestParams.BooleanSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.IsTrue(result.Content!["confirm"].GetBoolean());
    }

    [TestMethod]
    public async Task HandleAsync_StopsAnsweringOnceTheCapIsReached()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>
        {
            ["mailbox"] = Json("\"work\""),
        }));
        var coordinator = Create(new McpElicitationConfig { MaxPerCall = 2 }, responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var request = FormRequest(("mailbox", new ElicitRequestParams.StringSchema()));

        Assert.AreEqual(McpElicitationActions.Accept, (await coordinator.HandleAsync(request, CancellationToken.None)).Action);
        Assert.AreEqual(McpElicitationActions.Accept, (await coordinator.HandleAsync(request, CancellationToken.None)).Action);
        Assert.AreEqual(McpElicitationActions.Decline, (await coordinator.HandleAsync(request, CancellationToken.None)).Action);
        Assert.AreEqual(2, responder.Calls);
    }

    [TestMethod]
    public async Task HandleAsync_AnswersFromConfiguredDefaultsWithoutAResponder()
    {
        var config = new McpElicitationConfig
        {
            Defaults = { ["MAILBOX"] = Json("\"work\"") },
        };
        var coordinator = Create(config);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.AreEqual("work", result.Content!["mailbox"].GetString());
    }

    [TestMethod]
    public async Task HandleAsync_DropsADefaultThatNoLongerFitsTheSchema()
    {
        var config = new McpElicitationConfig
        {
            Defaults = { ["limit"] = Json("\"not a number\"") },
        };
        var coordinator = Create(config);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("limit", new ElicitRequestParams.NumberSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_DefaultsOverrideTheResponder()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>
        {
            ["mailbox"] = Json("\"guessed\""),
            ["limit"] = Json("5"),
        }));
        var config = new McpElicitationConfig
        {
            Defaults = { ["mailbox"] = Json("\"work\"") },
        };
        var coordinator = Create(config, responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(
                ("mailbox", new ElicitRequestParams.StringSchema()),
                ("limit", new ElicitRequestParams.NumberSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.AreEqual("work", result.Content!["mailbox"].GetString());
        Assert.AreEqual(5d, result.Content["limit"].GetDouble());
        CollectionAssert.AreEquivalent(new[] { "mailbox" }, responder.LastContext!.KnownValues.Keys.ToArray());
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenTheResponderAnswersWithTheWrongType()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(new Dictionary<string, JsonElement>
        {
            ["limit"] = Json("\"ten\""),
        }));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("limit", new ElicitRequestParams.NumberSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenTheResponderThrows()
    {
        var responder = new ThrowingResponder();
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenThereIsNoResponderAndNoDefaults()
    {
        var coordinator = Create();

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_GivesTheResponderTheInFlightCall()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Decline("no"));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search_mail", """{"query":"invoices"}""");
        await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual(1, responder.LastContext!.InFlightCalls.Count);
        Assert.AreEqual("search_mail", responder.LastContext.InFlightCalls[0].ToolName);
        StringAssert.Contains(responder.LastContext.InFlightCalls[0].Arguments!, "invoices");
        Assert.IsNull(responder.LastContext.InFlightCalls[0].SessionId);
    }

    [TestMethod]
    public async Task HandleAsync_GivesTheResponderTheCallingSession()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Decline("no"));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search_mail", "{}", "session-42");
        await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.AreEqual("session-42", scope.SessionId);
        Assert.AreEqual("session-42", responder.LastContext!.InFlightCalls[0].SessionId);
    }

    [TestMethod]
    public async Task HandleAsync_RecordsWhatHappenedOnTheCallScope()
    {
        var coordinator = Create();

        using var scope = coordinator.BeginCall("search", "{}");
        await coordinator.HandleAsync(
            FormRequest("Which mailbox?", ("mailbox", new ElicitRequestParams.StringSchema())),
            CancellationToken.None);

        Assert.IsTrue(scope.HasRecords);
        var record = scope.Records[0];
        Assert.AreEqual("Which mailbox?", record.Message);
        Assert.AreEqual(McpElicitationActions.Decline, record.Action);
        CollectionAssert.AreEquivalent(new[] { "mailbox" }, record.RequestedFields.ToArray());
    }

    [TestMethod]
    public async Task HandleAsync_StopsSeeingACallOnceItsScopeIsDisposed()
    {
        var responder = new StubResponder(_ => McpElicitationAnswer.Accept(
            new Dictionary<string, JsonElement> { ["mailbox"] = Json("\"work\"") }));
        var coordinator = Create(responder: responder);

        using (coordinator.BeginCall("search", "{}"))
        {
            // in flight
        }

        var result = await coordinator.HandleAsync(
            FormRequest(("mailbox", new ElicitRequestParams.StringSchema())), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls, "a disposed scope must no longer count as a call in flight");
    }

    // ── Hardening ─────────────────────────────────────────────────────────────

    private static StubResponder Accepting(params (string Name, string Json)[] values)
        => new(_ => McpElicitationAnswer.Accept(values.ToDictionary(v => v.Name, v => Json(v.Json))));

    private static ElicitRequestParams Form(
        (string Name, ElicitRequestParams.PrimitiveSchemaDefinition? Definition, bool Required)[] fields)
    {
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var (name, definition, _) in fields)
            properties[name] = definition!;

        return new ElicitRequestParams
        {
            Message = "Question",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = properties,
                Required = [.. fields.Where(f => f.Required).Select(f => f.Name)],
            },
        };
    }

    [TestMethod]
    public async Task HandleAsync_Declines_AFieldTheSdkCouldNotRead()
    {
        // SDK 1.4 turns {"type":"object","description":"Your GitHub password"} into a null
        // definition and discards the description, so nothing can tell it is a credential.
        var responder = Accepting(("value", "\"hunter2\""));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(Form([("value", null, true)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls);
        StringAssert.Contains(scope.Records[0].Reason!, "cannot read");
    }

    [TestMethod]
    [DataRow("yes", "no")]
    [DataRow("Confirm", "Cancel")]
    [DataRow("approve", "reject")]
    public async Task HandleAsync_Declines_AYesNoChoiceTheOperatorDidNotPreAnswer(string yes, string no)
    {
        var responder = Accepting(("confirm", $"\"{yes}\""));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("delete_rows", "{}");
        var result = await coordinator.HandleAsync(
            Form([("confirm", new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = [yes, no] }, true)]),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls);
    }

    [TestMethod]
    public async Task HandleAsync_AnswersAChoiceThatIsNotADecision()
    {
        var coordinator = Create(responder: Accepting(("mailbox", "\"work\"")));

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("mailbox", new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["work", "personal"] }, true)]),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
    }

    [TestMethod]
    public async Task HandleAsync_Declines_WhenARequiredDecisionSitsAlongsideData()
    {
        var responder = Accepting(("table", "\"invoices\""), ("confirm", "true"));
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("delete_rows", "{}");
        var result = await coordinator.HandleAsync(
            Form([
                ("table", new ElicitRequestParams.StringSchema(), true),
                ("confirm", new ElicitRequestParams.BooleanSchema(), true),
            ]),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.AreEqual(0, responder.Calls);
    }

    [TestMethod]
    public async Task HandleAsync_WithholdsAnOptionalDecisionTheResponderTriedToAnswer()
    {
        var coordinator = Create(responder: Accepting(("table", "\"invoices\""), ("confirm", "true")));

        using var scope = coordinator.BeginCall("delete_rows", "{}");
        var result = await coordinator.HandleAsync(
            Form([
                ("table", new ElicitRequestParams.StringSchema(), true),
                ("confirm", new ElicitRequestParams.BooleanSchema(), false),
            ]),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.AreEqual("invoices", result.Content!["table"].GetString());
        Assert.IsFalse(result.Content.ContainsKey("confirm"), "the server must apply its own default for the decision");
    }

    [TestMethod]
    public async Task HandleAsync_AnswersADecisionTheOperatorPreAnswered()
    {
        var config = new McpElicitationConfig { Defaults = new() { ["confirm"] = Json("true") } };
        var coordinator = Create(config, Accepting(("table", "\"invoices\"")));

        using var scope = coordinator.BeginCall("delete_rows", "{}");
        var result = await coordinator.HandleAsync(
            Form([
                ("table", new ElicitRequestParams.StringSchema(), true),
                ("confirm", new ElicitRequestParams.BooleanSchema(), true),
            ]),
            CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.IsTrue(result.Content!["confirm"].GetBoolean());
    }

    [TestMethod]
    public async Task HandleAsync_MatchesResponderKeysCaseInsensitively()
    {
        var coordinator = Create(responder: Accepting(("Mailbox", "\"work\"")));

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("mailbox", new ElicitRequestParams.StringSchema(), false)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.AreEqual("work", result.Content!["mailbox"].GetString());
    }

    [TestMethod]
    public async Task HandleAsync_Declines_AnAcceptThatSuppliesNoneOfTheFields()
    {
        var coordinator = Create(responder: Accepting(("somethingElse", "\"x\"")));

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("mailbox", new ElicitRequestParams.StringSchema(), false)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
        Assert.IsNull(result.Content);
    }

    [TestMethod]
    public async Task TryCreate_ToleratesNullDefaultsAndDeniedFields()
    {
        // "defaults": null in mcp.json used to throw during connect, surfacing as a
        // connectivity failure that the reconnect sweep retried forever.
        var coordinator = Create(
            new McpElicitationConfig { Defaults = null!, DeniedFields = null! },
            Accepting(("mailbox", "\"work\"")));

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("mailbox", new ElicitRequestParams.StringSchema(), true)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
    }

    [TestMethod]
    [DataRow("true", "flag", "boolean")]
    [DataRow("5", "limit", "integer")]
    public async Task HandleAsync_ReadsAStringDefaultAsTheLiteralItSpells(string configured, string field, string type)
    {
        // Defaults bound from appsettings or environment variables are always strings.
        var config = new McpElicitationConfig
        {
            Defaults = new() { [field] = JsonSerializer.SerializeToElement(configured) },
        };
        var coordinator = Create(config);

        ElicitRequestParams.PrimitiveSchemaDefinition definition = type == "boolean"
            ? new ElicitRequestParams.BooleanSchema()
            : new ElicitRequestParams.NumberSchema { Type = "integer" };

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(Form([(field, definition, true)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Accept, result.Action);
        Assert.AreEqual(configured, result.Content![field].GetRawText());
    }

    [TestMethod]
    public async Task HandleAsync_DoesNotCoerceResponderOutput()
    {
        var coordinator = Create(responder: Accepting(("limit", "\"5\"")));

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("limit", new ElicitRequestParams.NumberSchema { Type = "integer" }, true)]), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task HandleAsync_LetsOnlyOneConcurrentQuestionThroughTheLastRound()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var responder = new GatedResponder(gate.Task, () => Interlocked.Increment(ref calls));
        var coordinator = Create(new McpElicitationConfig { MaxPerCall = 1 }, responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var request = Form([("mailbox", new ElicitRequestParams.StringSchema(), true)]);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => coordinator.HandleAsync(request, CancellationToken.None).AsTask()))
            .ToArray();

        // Every task but the one inside the responder finishes on its own.
        while (tasks.Count(t => t.IsCompleted) < 7)
            await Task.Delay(10);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.AreEqual(1, calls, "the cap must be enforced atomically");
        Assert.AreEqual(1, results.Count(r => r.Action == McpElicitationActions.Accept));
    }

    [TestMethod]
    public async Task HandleAsync_AnswersCancel_WhenTheSdkWithdrawsTheRequest()
    {
        using var cts = new CancellationTokenSource();
        var responder = new GatedResponder(Task.Delay(Timeout.Infinite, cts.Token), () => cts.Cancel());
        var coordinator = Create(responder: responder);

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(
            Form([("mailbox", new ElicitRequestParams.StringSchema(), true)]), cts.Token);

        Assert.AreEqual(McpElicitationActions.Cancel, result.Action);
        Assert.AreEqual(McpElicitationActions.Cancel, scope.Records.Single().Action,
            "the agent should still hear that a question was asked");
    }

    /// <summary>Answers "work" for mailbox once <paramref name="gate"/> completes.</summary>
    private sealed class GatedResponder(Task gate, Action onCall) : IMcpElicitationResponder
    {
        public async ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
        {
            onCall();
            await gate.WaitAsync(ct);
            return McpElicitationAnswer.Accept(new Dictionary<string, JsonElement> { ["mailbox"] = Json("\"work\"") });
        }
    }

    private sealed class ThrowingResponder : IMcpElicitationResponder
    {
        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
