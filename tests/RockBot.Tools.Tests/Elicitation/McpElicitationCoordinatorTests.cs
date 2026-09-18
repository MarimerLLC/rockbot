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
        var coordinator = Create(new McpElicitationConfig { Mode = "yolo" });

        using var scope = coordinator.BeginCall("search", "{}");
        var result = await coordinator.HandleAsync(FormRequest(), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
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
        var coordinator = Create();

        using (coordinator.BeginCall("search", "{}"))
        {
            // in flight
        }

        var result = await coordinator.HandleAsync(FormRequest(), CancellationToken.None);

        Assert.AreEqual(McpElicitationActions.Decline, result.Action);
    }

    private sealed class ThrowingResponder : IMcpElicitationResponder
    {
        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
