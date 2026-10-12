using System.Text.Json;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>
/// The values behind a handed-back MCP question's form (#660): defaults, the checks made before
/// sending, and the answer typed the way the server's schema asks. The bridge re-validates; these
/// checks only spare the user a refused round trip.
/// </summary>
[TestClass]
public class McpQuestionFormModelTests
{
    private static McpQuestionForm Form(params McpQuestionField[] fields) => new()
    {
        QuestionId = "q_1",
        ServerName = "fixture",
        ToolName = "tool",
        Message = "Answer, please.",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        Fields = fields,
    };

    private static McpQuestionField Field(string name, string kind, bool required = false) =>
        new() { Name = name, Kind = kind, Required = required };

    private static JsonElement Answers(McpQuestionFormModel model) =>
        JsonDocument.Parse(model.BuildAnswersJson()).RootElement;

    [TestMethod]
    public void TheFormIsReadFromStructuredData_AndAnUnreadableOneIsNull()
    {
        var json = JsonSerializer.Serialize(Form(Field("a", McpQuestionFieldKind.String)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("q_1", McpQuestionFormModel.TryParseForm(json)?.QuestionId);
        Assert.IsNull(McpQuestionFormModel.TryParseForm(null));
        Assert.IsNull(McpQuestionFormModel.TryParseForm("{ not json"));
        Assert.IsNull(McpQuestionFormModel.TryParseForm("{}"), "a form without a question id can't be answered");
    }

    [TestMethod]
    public void Answers_AreTypedAsTheSchemaAsks()
    {
        var model = new McpQuestionFormModel(Form(
            Field("name", McpQuestionFieldKind.String),
            Field("count", McpQuestionFieldKind.Integer),
            Field("ratio", McpQuestionFieldKind.Number),
            Field("ok", McpQuestionFieldKind.Boolean),
            Field("pick", McpQuestionFieldKind.SingleSelect) with { Options = [new("a", "A"), new("b", "B")] },
            Field("many", McpQuestionFieldKind.MultiSelect) with { Options = [new("x", "X"), new("y", "Y"), new("z", "Z")] }));

        model.SetText("name", "Ada");
        model.SetText("count", "3");
        model.SetText("ratio", "0.5");
        model.SetFlag("ok", false);
        model.SetText("pick", "b");
        model.SetChosen("many", "z", true);
        model.SetChosen("many", "x", true);

        Assert.AreEqual(0, model.Validate().Count);
        var answers = Answers(model);
        Assert.AreEqual("Ada", answers.GetProperty("name").GetString());
        Assert.AreEqual(3, answers.GetProperty("count").GetInt64());
        Assert.AreEqual(0.5, answers.GetProperty("ratio").GetDouble());
        Assert.AreEqual(JsonValueKind.False, answers.GetProperty("ok").ValueKind);
        Assert.AreEqual("b", answers.GetProperty("pick").GetString());
        CollectionAssert.AreEqual(new[] { "x", "z" },
            answers.GetProperty("many").EnumerateArray().Select(v => v.GetString()).ToArray(),
            "in the server's option order");
    }

    [TestMethod]
    public void OptionalFieldsLeftEmpty_AreLeftOut()
    {
        var model = new McpQuestionFormModel(Form(
            Field("note", McpQuestionFieldKind.String),
            Field("count", McpQuestionFieldKind.Integer),
            Field("ok", McpQuestionFieldKind.Boolean),
            Field("many", McpQuestionFieldKind.MultiSelect) with { Options = [new("x", "X")] }));

        Assert.AreEqual(0, model.Validate().Count);
        Assert.AreEqual("{}", model.BuildAnswersJson());
    }

    [TestMethod]
    public void RequiredFields_MustBeAnswered()
    {
        var model = new McpQuestionFormModel(Form(
            Field("name", McpQuestionFieldKind.String, required: true),
            Field("count", McpQuestionFieldKind.Number, required: true),
            Field("ok", McpQuestionFieldKind.Boolean, required: true),
            Field("pick", McpQuestionFieldKind.SingleSelect, required: true) with { Options = [new("a", "A")] },
            Field("many", McpQuestionFieldKind.MultiSelect, required: true) with { Options = [new("x", "X")] }));

        var errors = model.Validate();

        CollectionAssert.AreEquivalent(new[] { "name", "count", "ok", "pick", "many" }, errors.Keys.ToArray());
    }

    [TestMethod]
    public void Limits_AreChecked()
    {
        var model = new McpQuestionFormModel(Form(
            Field("size", McpQuestionFieldKind.Integer) with { Minimum = 1, Maximum = 12 },
            Field("name", McpQuestionFieldKind.String) with { MinLength = 3, MaxLength = 5 },
            Field("many", McpQuestionFieldKind.MultiSelect) with
            {
                Options = [new("x", "X"), new("y", "Y"), new("z", "Z")], MinItems = 2, MaxItems = 2,
            }));

        model.SetText("size", "13");
        model.SetText("name", "ab");
        model.SetChosen("many", "x", true);
        var errors = model.Validate();
        StringAssert.Contains(errors["size"], "maximum is 12");
        StringAssert.Contains(errors["name"], "at least 3");
        StringAssert.Contains(errors["many"], "at least 2");

        model.SetText("size", "2.5");
        model.SetText("name", "abcdef");
        model.SetChosen("many", "y", true);
        model.SetChosen("many", "z", true);
        errors = model.Validate();
        StringAssert.Contains(errors["size"], "whole number");
        StringAssert.Contains(errors["name"], "at most 5");
        StringAssert.Contains(errors["many"], "at most 2");

        model.SetText("size", "12");
        model.SetText("name", "abc");
        model.SetChosen("many", "z", false);
        Assert.AreEqual(0, model.Validate().Count);
    }

    [TestMethod]
    public void Formats_AreChecked()
    {
        var model = new McpQuestionFormModel(Form(
            Field("email", McpQuestionFieldKind.String) with { Format = "email" },
            Field("site", McpQuestionFieldKind.String) with { Format = "uri" },
            Field("day", McpQuestionFieldKind.String) with { Format = "date" },
            Field("at", McpQuestionFieldKind.String) with { Format = "date-time" }));

        model.SetText("email", "nobody");
        model.SetText("site", "example");
        model.SetText("day", "12/10/2026");
        model.SetText("at", "tomorrow");
        Assert.AreEqual(4, model.Validate().Count);

        model.SetText("email", "ada@example.com");
        model.SetText("site", "https://example.com/x");
        model.SetText("day", "2026-10-12");
        model.SetText("at", "2026-10-12T14:30:00Z");
        Assert.AreEqual(0, model.Validate().Count);
    }

    [TestMethod]
    public void AChoiceOutsideTheOptions_IsRefused()
    {
        var model = new McpQuestionFormModel(Form(
            Field("pick", McpQuestionFieldKind.SingleSelect) with { Options = [new("a", "A")] }));

        model.SetText("pick", "b");

        StringAssert.Contains(model.Validate()["pick"], "one of the options");
    }

    [TestMethod]
    public void TheServersDefaults_ArePrefilled_ButNeverForADecision()
    {
        var model = new McpQuestionFormModel(Form(
            Field("name", McpQuestionFieldKind.String) with { Default = "\"Ada\"" },
            Field("count", McpQuestionFieldKind.Integer) with { Default = "2" },
            Field("notify", McpQuestionFieldKind.Boolean) with { Default = "true" },
            Field("confirm", McpQuestionFieldKind.Boolean) with { Default = "true", IsDecision = true },
            Field("go", McpQuestionFieldKind.SingleSelect) with
            {
                Options = [new("yes", "Yes"), new("no", "No")], Default = "\"yes\"", IsDecision = true,
            },
            Field("many", McpQuestionFieldKind.MultiSelect) with
            {
                Options = [new("x", "X"), new("y", "Y")], Default = """["y","bogus"]""",
            }));

        Assert.AreEqual("Ada", model.GetText("name"));
        Assert.AreEqual("2", model.GetText("count"));
        Assert.AreEqual(true, model.GetFlag("notify"));
        Assert.IsNull(model.GetFlag("confirm"), "a decision is the user's to make");
        Assert.IsNull(model.GetText("go"));
        Assert.IsTrue(model.IsChosen("many", "y"));
        Assert.IsFalse(model.IsChosen("many", "bogus"), "a default outside the options is dropped");
    }
}
