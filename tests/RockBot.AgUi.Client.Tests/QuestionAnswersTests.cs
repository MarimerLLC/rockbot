using RockBot.UserProxy;

namespace RockBot.AgUi.Client.Tests;

/// <summary>The answer a generated form sends: the right JSON type for each kind of field.</summary>
[TestClass]
public sealed class QuestionAnswersTests
{
    private static McpQuestionForm Form(params McpQuestionField[] fields) => new()
    {
        QuestionId = "q1", ServerName = "scheduler", ToolName = "create_event", Message = "?",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Fields = fields,
    };

    private static readonly McpQuestionOption[] Days = [new("mon", "Monday"), new("tue", "Tuesday"), new("wed", "Wednesday")];

    [TestMethod]
    public void EachKindOfField_IsSentAsItsJsonType()
    {
        var answers = new QuestionAnswers(Form(
            new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Required = true },
            new McpQuestionField { Name = "seats", Kind = McpQuestionFieldKind.Integer },
            new McpQuestionField { Name = "budget", Kind = McpQuestionFieldKind.Number },
            new McpQuestionField { Name = "notify", Kind = McpQuestionFieldKind.Boolean },
            new McpQuestionField { Name = "room", Kind = McpQuestionFieldKind.SingleSelect, Options = [new("a", "Room A")] },
            new McpQuestionField { Name = "days", Kind = McpQuestionFieldKind.MultiSelect, Options = Days },
            new McpQuestionField { Name = "note", Kind = McpQuestionFieldKind.String }));

        answers.Text["tz"] = " Europe/Brussels ";
        answers.Text["seats"] = "12";
        answers.Text["budget"] = "99.5";
        answers.Flags["notify"] = true;
        answers.Text["room"] = "a";
        answers.Choices["days"].Add("wed");
        answers.Choices["days"].Add("mon");

        var json = answers.Build(out var problem)!;

        Assert.IsNull(problem);
        Assert.AreEqual(
            """{"tz":"Europe/Brussels","seats":12,"budget":99.5,"notify":true,"room":"a","days":["mon","wed"]}""",
            json.ToJsonString(),
            "the optional note that was left empty is left out");
    }

    [TestMethod]
    public void Defaults_PreFillTheForm()
    {
        var answers = new QuestionAnswers(Form(
            new McpQuestionField { Name = "length", Kind = McpQuestionFieldKind.SingleSelect, Default = "\"30\"" },
            new McpQuestionField { Name = "seats", Kind = McpQuestionFieldKind.Integer, Default = "4" },
            new McpQuestionField { Name = "notify", Kind = McpQuestionFieldKind.Boolean, Default = "true" },
            new McpQuestionField { Name = "days", Kind = McpQuestionFieldKind.MultiSelect, Options = Days, Default = "[\"tue\"]" },
            new McpQuestionField { Name = "odd", Kind = McpQuestionFieldKind.String, Default = "not json" }));

        Assert.AreEqual("""{"length":"30","seats":4,"notify":true,"days":["tue"]}""", answers.Build(out _)!.ToJsonString());
    }

    [TestMethod]
    public void ARequiredFieldLeftEmpty_OrANumberThatIsNotOne_StopsTheAnswer()
    {
        var answers = new QuestionAnswers(Form(
            new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true },
            new McpQuestionField { Name = "seats", Kind = McpQuestionFieldKind.Integer, Title = "Seats" }));

        Assert.IsNull(answers.Build(out var missing));
        Assert.AreEqual("Time zone is required.", missing);

        answers.Text["tz"] = "UTC";
        answers.Text["seats"] = "a few";
        Assert.IsNull(answers.Build(out var notANumber));
        Assert.AreEqual("Seats must be a whole number.", notANumber);
    }

    [TestMethod]
    public void ASingleYesNoDecision_IsRecognised()
    {
        var decision = new QuestionAnswers(Form(
            new McpQuestionField { Name = "approve", Kind = McpQuestionFieldKind.Boolean, IsDecision = true }));
        decision.Flags["approve"] = true;

        Assert.IsTrue(decision.IsDecision);
        Assert.AreEqual("""{"approve":true}""", decision.Build(out _)!.ToJsonString());
        Assert.IsFalse(new QuestionAnswers(Form(new McpQuestionField { Name = "notify", Kind = McpQuestionFieldKind.Boolean })).IsDecision);
    }
}
