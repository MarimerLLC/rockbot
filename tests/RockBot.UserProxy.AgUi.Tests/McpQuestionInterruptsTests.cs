using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>An <see cref="McpQuestionForm"/> as an AG-UI interrupt: its reason, its answer schema and its fields.</summary>
[TestClass]
public sealed class McpQuestionInterruptsTests
{
    private static McpQuestionForm Form(params McpQuestionField[] fields) => new()
    {
        QuestionId = "q1", ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
        ExpiresAt = new DateTimeOffset(2026, 11, 14, 11, 30, 0, TimeSpan.FromHours(1)),
        Fields = fields,
    };

    private static McpQuestionField Field(string kind, string name = "f") => new() { Name = name, Kind = kind };

    /// <summary>The schema of the form's single field.</summary>
    private static JsonElement FieldSchema(McpQuestionField field) =>
        McpQuestionInterrupts.ResponseSchema(Form(field)).GetProperty("properties").GetProperty(field.Name);

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static string[] Keywords(JsonElement schema) => [.. schema.EnumerateObject().Select(p => p.Name).Order()];

    // ── The schema ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Schema_IsAnObjectWithAPropertyPerField_AndTheRequiredOnes()
    {
        var schema = McpQuestionInterrupts.ResponseSchema(Form(
            Field(McpQuestionFieldKind.String, "tz") with { Required = true },
            Field(McpQuestionFieldKind.Boolean, "notify"),
            Field(McpQuestionFieldKind.Integer, "count") with { Required = true }));

        Assert.AreEqual("object", schema.GetProperty("type").GetString());
        CollectionAssert.AreEqual(
            new[] { "tz", "notify", "count" },
            schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray(),
            "the form's order");
        CollectionAssert.AreEqual(new[] { "tz", "count" }, Strings(schema.GetProperty("required")));
    }

    [TestMethod]
    public void Schema_WithNothingRequired_HasNoRequiredKeyword()
    {
        var schema = McpQuestionInterrupts.ResponseSchema(Form(Field(McpQuestionFieldKind.String)));

        Assert.IsFalse(schema.TryGetProperty("required", out _));
    }

    [TestMethod]
    public void Schema_OfAFormWithoutFields_IsAnEmptyObjectSchema()
    {
        var schema = McpQuestionInterrupts.ResponseSchema(Form());

        Assert.AreEqual("object", schema.GetProperty("type").GetString());
        Assert.AreEqual(0, schema.GetProperty("properties").EnumerateObject().Count());
    }

    [TestMethod]
    public void StringField_CarriesItsLengthsAndFormat()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.String) with { MinLength = 2, MaxLength = 40, Format = "email" });

        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        Assert.AreEqual(2, schema.GetProperty("minLength").GetInt32());
        Assert.AreEqual(40, schema.GetProperty("maxLength").GetInt32());
        Assert.AreEqual("email", schema.GetProperty("format").GetString());
    }

    [TestMethod]
    public void PlainStringField_IsJustAType()
    {
        CollectionAssert.AreEqual(new[] { "type" }, Keywords(FieldSchema(Field(McpQuestionFieldKind.String))));
    }

    [TestMethod]
    [DataRow(McpQuestionFieldKind.Number)]
    [DataRow(McpQuestionFieldKind.Integer)]
    public void NumericField_CarriesItsRange(string kind)
    {
        var schema = FieldSchema(Field(kind) with { Minimum = 1, Maximum = 10.5 });

        Assert.AreEqual(kind, schema.GetProperty("type").GetString());
        Assert.AreEqual(1d, schema.GetProperty("minimum").GetDouble());
        Assert.AreEqual(10.5, schema.GetProperty("maximum").GetDouble());
        // Lengths belong to strings; they aren't copied onto a number.
        Assert.IsFalse(schema.TryGetProperty("minLength", out _));
    }

    [TestMethod]
    public void BooleanField_IsABoolean()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.Boolean) with { IsDecision = true });

        CollectionAssert.AreEqual(new[] { "type" }, Keywords(schema));
        Assert.AreEqual("boolean", schema.GetProperty("type").GetString());
    }

    [TestMethod]
    public void SingleSelect_ListsItsValues_AndTheirLabels()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.SingleSelect) with
        {
            Options = [new McpQuestionOption("eu", "Europe"), new McpQuestionOption("us", "United States")],
        });

        Assert.AreEqual("string", schema.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "eu", "us" }, Strings(schema.GetProperty("enum")));
        var titled = schema.GetProperty("oneOf");
        Assert.AreEqual(2, titled.GetArrayLength());
        Assert.AreEqual("us", titled[1].GetProperty("const").GetString());
        Assert.AreEqual("United States", titled[1].GetProperty("title").GetString());
    }

    [TestMethod]
    public void SingleSelect_WhoseLabelsAreItsValues_OnlyHasTheEnum()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.SingleSelect) with
        {
            Options = [new McpQuestionOption("eu", "eu"), new McpQuestionOption("us", "us")],
        });

        CollectionAssert.AreEqual(new[] { "enum", "type" }, Keywords(schema));
    }

    [TestMethod]
    public void MultiSelect_IsAnArrayOfItsValues_WithItsBounds()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.MultiSelect) with
        {
            Options = [new McpQuestionOption("mon", "Monday"), new McpQuestionOption("tue", "Tuesday")],
            MinItems = 1, MaxItems = 2,
        });

        Assert.AreEqual("array", schema.GetProperty("type").GetString());
        Assert.IsTrue(schema.GetProperty("uniqueItems").GetBoolean());
        Assert.AreEqual(1, schema.GetProperty("minItems").GetInt32());
        Assert.AreEqual(2, schema.GetProperty("maxItems").GetInt32());

        var items = schema.GetProperty("items");
        Assert.AreEqual("string", items.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "mon", "tue" }, Strings(items.GetProperty("enum")));
        Assert.AreEqual("Tuesday", items.GetProperty("anyOf")[1].GetProperty("title").GetString());
        Assert.IsFalse(schema.TryGetProperty("enum", out _), "the choices constrain the items, not the array");
    }

    [TestMethod]
    public void SelectWithoutOptions_StillHasItsType()
    {
        Assert.AreEqual("string", FieldSchema(Field(McpQuestionFieldKind.SingleSelect)).GetProperty("type").GetString());
        var multi = FieldSchema(Field(McpQuestionFieldKind.MultiSelect));
        Assert.AreEqual("array", multi.GetProperty("type").GetString());
        Assert.IsFalse(multi.GetProperty("items").TryGetProperty("enum", out _));
    }

    [TestMethod]
    public void AKindThisProxyDoesNotKnow_IsUnconstrained_ButKeepsItsTitle()
    {
        var schema = FieldSchema(Field("date-range") with { Title = "When", Required = true });

        CollectionAssert.AreEqual(new[] { "title" }, Keywords(schema));
    }

    [TestMethod]
    public void TitleDescriptionAndDefault_AreCopied()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.Integer) with
        {
            Title = "Seats", Description = "How many to book", Default = "4",
        });

        Assert.AreEqual("Seats", schema.GetProperty("title").GetString());
        Assert.AreEqual("How many to book", schema.GetProperty("description").GetString());
        Assert.AreEqual(4, schema.GetProperty("default").GetInt32());
    }

    [TestMethod]
    [DataRow("\"Europe/Brussels\"", JsonValueKind.String)]
    [DataRow("true", JsonValueKind.True)]
    [DataRow("[\"mon\"]", JsonValueKind.Array)]
    public void ADefault_IsJson_WhateverItsKind(string json, JsonValueKind kind)
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.String) with { Default = json });

        Assert.AreEqual(kind, schema.GetProperty("default").ValueKind);
    }

    [TestMethod]
    public void ADefaultThatIsNotJson_IsLeftOut()
    {
        var schema = FieldSchema(Field(McpQuestionFieldKind.String) with { Default = "Europe/Brussels" });

        Assert.IsFalse(schema.TryGetProperty("default", out _));
    }

    [TestMethod]
    public void ARepeatedFieldName_KeepsTheFirst()
    {
        var schema = McpQuestionInterrupts.ResponseSchema(Form(
            Field(McpQuestionFieldKind.String, "x") with { Required = true },
            Field(McpQuestionFieldKind.Boolean, "x") with { Required = true }));

        Assert.AreEqual("string", schema.GetProperty("properties").GetProperty("x").GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "x" }, Strings(schema.GetProperty("required")));
    }

    // ── The reason ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ASingleDecision_IsAConfirmation()
    {
        var form = Form(Field(McpQuestionFieldKind.Boolean, "approve") with { IsDecision = true });

        Assert.AreEqual(InterruptReasons.Confirmation, McpQuestionInterrupts.Reason(form));
    }

    [TestMethod]
    public void AnythingElse_IsInputRequired()
    {
        var decision = Field(McpQuestionFieldKind.Boolean, "approve") with { IsDecision = true };

        // A plain checkbox is a value, not a decision.
        Assert.AreEqual(InterruptReasons.InputRequired, McpQuestionInterrupts.Reason(Form(Field(McpQuestionFieldKind.Boolean))));
        // A decision among other fields is a form to fill in.
        Assert.AreEqual(InterruptReasons.InputRequired,
            McpQuestionInterrupts.Reason(Form(decision, Field(McpQuestionFieldKind.String, "why"))));
        Assert.AreEqual(InterruptReasons.InputRequired, McpQuestionInterrupts.Reason(Form(Field(McpQuestionFieldKind.String))));
        Assert.AreEqual(InterruptReasons.InputRequired, McpQuestionInterrupts.Reason(Form()));
        // Marked a decision, but not a yes/no.
        Assert.AreEqual(InterruptReasons.InputRequired,
            McpQuestionInterrupts.Reason(Form(Field(McpQuestionFieldKind.String) with { IsDecision = true })));
    }

    // ── The interrupt ────────────────────────────────────────────────────────

    [TestMethod]
    public void Interrupt_IsTheQuestion()
    {
        var form = Form(Field(McpQuestionFieldKind.String, "tz") with { Title = "Time zone", Required = true });

        var interrupt = McpQuestionInterrupts.ToInterrupt(form);

        Assert.AreEqual("q1", interrupt.Id);
        Assert.AreEqual(InterruptReasons.InputRequired, interrupt.Reason);
        Assert.AreEqual("Which time zone?", interrupt.Message);
        // ISO 8601, in UTC whatever offset the form used.
        Assert.AreEqual("2026-11-14T10:30:00.000Z", interrupt.ExpiresAt);
        Assert.AreEqual(form.ExpiresAt, DateTimeOffset.Parse(interrupt.ExpiresAt!));
        Assert.AreEqual("string", interrupt.ResponseSchema!.Value.GetProperty("properties").GetProperty("tz").GetProperty("type").GetString());
        Assert.IsNull(interrupt.ToolCallId, "the question is an MCP server's, not a tool approval");

        var rockbot = interrupt.Metadata!.Value.GetProperty("rockbot");
        Assert.AreEqual("scheduler", rockbot.GetProperty("serverName").GetString());
        Assert.AreEqual("create_event", rockbot.GetProperty("toolName").GetString());
        Assert.AreEqual("Time zone", rockbot.GetProperty("form").GetProperty("fields")[0].GetProperty("title").GetString());
    }

    [TestMethod]
    public void Interrupt_RoundTripsThroughTheSdk_InARunFinishedEvent()
    {
        var interrupt = McpQuestionInterrupts.ToInterrupt(
            Form(Field(McpQuestionFieldKind.Boolean, "approve") with { IsDecision = true }));
        var finished = new TurnEventMapper("agui-session", "run-1").Finish(
            new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = "run-1" }, [interrupt]).Single();

        var json = AgUiJson.Serialize(finished);
        using var document = JsonDocument.Parse(json);
        var outcome = document.RootElement.GetProperty("outcome");
        Assert.AreEqual("interrupt", outcome.GetProperty("type").GetString());
        Assert.AreEqual("confirmation", outcome.GetProperty("interrupts")[0].GetProperty("reason").GetString());

        var parsed = (RunFinishedEvent)JsonSerializer.Deserialize(json, AGUIJsonSerializerContext.Default.BaseEvent)!;
        var read = ((RunFinishedInterruptOutcome)parsed.Outcome!).Interrupts.Single();
        Assert.AreEqual("q1", read.Id);
        Assert.AreEqual(interrupt.ExpiresAt, read.ExpiresAt);
        Assert.AreEqual("boolean", read.ResponseSchema!.Value.GetProperty("properties").GetProperty("approve").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Finish_WithNoInterrupts_IsAPlainSuccess()
    {
        var finished = (RunFinishedEvent)new TurnEventMapper("agui-session", "run-1").Finish(
            new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = "run-1" }, []).Single();

        Assert.IsNull(finished.Outcome);
    }

    [TestMethod]
    public void Finish_OfARunThatFailed_CarriesNoInterrupts()
    {
        var interrupt = McpQuestionInterrupts.ToInterrupt(Form());
        var mapper = new TurnEventMapper("agui-session", "run-1");

        var cancelled = (RunFinishedEvent)mapper.Finish(
            new TurnResult { Outcome = TurnResultOutcome.Cancelled, CorrelationId = "run-1" }, [interrupt]).Single();
        var failed = mapper.Finish(
            new TurnResult { Outcome = TurnResultOutcome.Error, CorrelationId = "run-1", Error = "boom" }, [interrupt]).Single();

        Assert.IsInstanceOfType<RunFinishedCancelledOutcome>(cancelled.Outcome);
        Assert.IsInstanceOfType<RunErrorEvent>(failed);
    }

    // ── Reading the form ─────────────────────────────────────────────────────

    [TestMethod]
    public void TheAgentsFormJson_IsRead()
    {
        var json = JsonSerializer.Serialize(Form(Field(McpQuestionFieldKind.String, "tz")), TurnEventMapper.PayloadJson);

        var form = McpQuestionInterrupts.TryParseForm(json);

        Assert.AreEqual("q1", form?.QuestionId);
        Assert.AreEqual("tz", form?.Fields.Single().Name);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("{not json")]
    [DataRow("[]")]
    [DataRow("""{"message":"no id"}""")]
    public void WhatIsNotAForm_IsNotRead(string? json)
    {
        Assert.IsNull(McpQuestionInterrupts.TryParseForm(json));
    }
}
