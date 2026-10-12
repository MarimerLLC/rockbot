using System.Text.Json;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// The chat's handed-back MCP question forms (#660): a form arrives mid-turn without ending it,
/// status messages close it (and only <c>rejected</c> leaves it open), a submission waits on the
/// agent, and an open form survives a reload.
/// </summary>
[TestClass]
public sealed class ChatStateServiceMcpQuestionTests
{
    private const string QuestionId = "q_1";

    private readonly ChatStateService _sut = new();
    private readonly BlazorUserFrontend _frontend;

    public ChatStateServiceMcpQuestionTests() => _frontend = new BlazorUserFrontend(_sut);

    private static AgentReply FormReply(string questionId = QuestionId, DateTimeOffset? expiresAt = null) => new()
    {
        Content = "The MCP server \"mail\" asks, for search_mail: Which mailbox?",
        SessionId = "blazor-session",
        AgentName = "mcp-bridge",
        IsFinal = true,
        ContentType = McpQuestionForm.ContentType,
        StructuredData = JsonSerializer.Serialize(new McpQuestionForm
        {
            QuestionId = questionId,
            ServerName = "mail",
            ToolName = "search_mail",
            Message = "Which mailbox?",
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30),
            Fields = [new McpQuestionField { Name = "mailbox", Kind = McpQuestionFieldKind.String, Required = true }],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private static McpQuestionStatus Status(string state, string? answeredBy = null, string? reason = null) => new()
    {
        SessionId = "blazor-session",
        QuestionId = QuestionId,
        State = state,
        AnsweredBy = answeredBy,
        Reason = reason,
    };

    private ChatMessage Question => _sut.Messages.Single(m => m.Category == MessageCategory.McpQuestion);

    [TestMethod]
    public async Task AFormArrivesMidTurn_WithoutEndingIt()
    {
        _sut.SetProcessing(true);

        await _frontend.DisplayReplyAsync(FormReply());

        Assert.IsTrue(_sut.IsProcessing, "the agent is still writing its reply when the form arrives");
        Assert.AreEqual(QuestionId, Question.Question!.QuestionId);
        Assert.IsTrue(Question.IsQuestionOpen(DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task AFormReply_DoesNotTeachTheFrontendTheAgentsName()
    {
        await _frontend.DisplayReplyAsync(FormReply());
        await _frontend.DisplayStatusAsync(new AgentReply
        {
            Content = "Searching…", SessionId = "blazor-session", AgentName = "rockbot", IsFinal = false,
        });

        // Had "mcp-bridge" been learned as the primary agent, rockbot's progress would be A2A.
        var log = _sut.Messages.Single(m => m.IsActivityLog);
        Assert.AreEqual(MessageCategory.PrimaryProgress, log.Category);
    }

    [TestMethod]
    public async Task AFormThatCantBeRead_ShowsItsText()
    {
        await _frontend.DisplayReplyAsync(FormReply() with { StructuredData = "not json" });

        Assert.IsNull(Question.Question);
        StringAssert.Contains(Question.Content, "Which mailbox?");
    }

    [TestMethod]
    public async Task TheSameQuestion_IsShownOnce()
    {
        await _frontend.DisplayReplyAsync(FormReply());
        await _frontend.DisplayReplyAsync(FormReply());

        Assert.AreEqual(1, _sut.Messages.Count(m => m.Category == MessageCategory.McpQuestion));
    }

    [TestMethod]
    public async Task AStatusClosesTheForm_AndAClosedFormStaysClosed()
    {
        await _frontend.DisplayReplyAsync(FormReply());

        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Answered, answeredBy: "agent"));
        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Rejected, reason: "late"));

        Assert.AreEqual(McpQuestionState.Answered, Question.QuestionState);
        Assert.AreEqual("agent", Question.QuestionAnsweredBy);
        Assert.IsFalse(Question.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.IsFalse(_sut.TryBeginQuestionSubmit(QuestionId), "a closed form can't be sent");
    }

    [TestMethod]
    public async Task AStatusForAQuestionNotShown_IsIgnored()
    {
        await _frontend.DisplayReplyAsync(FormReply());

        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Expired) with { QuestionId = "q_other" });

        Assert.IsNull(Question.QuestionState);
    }

    [TestMethod]
    public async Task ASubmission_IsSentOnce_AndWaitsOnTheAgent()
    {
        await _frontend.DisplayReplyAsync(FormReply());

        Assert.IsTrue(_sut.TryBeginQuestionSubmit(QuestionId));
        Assert.IsFalse(_sut.TryBeginQuestionSubmit(QuestionId), "a second tab can't send it again");
        Assert.IsTrue(_sut.IsProcessing, "sending the form makes the chat wait for the agent");

        // Accepted: the form closes, but the user still waits for the agent's reply.
        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Answered, answeredBy: "user"));
        Assert.IsFalse(Question.IsSubmitting);
        Assert.IsTrue(_sut.IsProcessing);

        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "Found 3 invoices.", SessionId = "blazor-session", AgentName = "rockbot", IsFinal = true,
            FollowsQuestionId = QuestionId,
        });
        Assert.IsFalse(_sut.IsProcessing);
    }

    [TestMethod]
    public async Task TheReplyOfTheTurnThatShowedTheForm_DoesNotEndTheFormsWait()
    {
        // The form arrives while its turn is still running, and the user answers at once.
        _sut.SetProcessing(true);
        await _frontend.DisplayReplyAsync(FormReply());
        _sut.TryBeginQuestionSubmit(QuestionId);

        // That turn finishes: its reply, and its send loop's own SetProcessing(false).
        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "There's a form waiting for you.", SessionId = "blazor-session", AgentName = "rockbot", IsFinal = true,
        });
        _sut.SetProcessing(false);
        Assert.IsTrue(_sut.IsProcessing, "the form's follow-up is still to come");

        await _frontend.DisplayReplyAsync(new AgentReply
        {
            Content = "Found 3 invoices.", SessionId = "blazor-session", AgentName = "rockbot", IsFinal = true,
            FollowsQuestionId = QuestionId,
        });
        Assert.IsFalse(_sut.IsProcessing);
    }

    [TestMethod]
    public async Task Cancelling_StopsWaitingOnAFormAnswerToo()
    {
        await _frontend.DisplayReplyAsync(FormReply());
        _sut.TryBeginQuestionSubmit(QuestionId);

        _sut.CancelProcessing();

        Assert.IsFalse(_sut.IsProcessing);
    }

    [TestMethod]
    public async Task ARejectedSubmission_ReopensTheForm_AndEndsTheWait()
    {
        await _frontend.DisplayReplyAsync(FormReply());
        _sut.TryBeginQuestionSubmit(QuestionId);

        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Rejected, reason: "mailbox is too short"));

        Assert.IsFalse(_sut.IsProcessing, "no agent turn follows a refusal");
        Assert.IsFalse(Question.IsSubmitting);
        Assert.AreEqual("mailbox is too short", Question.QuestionReason);
        Assert.IsTrue(Question.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.IsTrue(_sut.TryBeginQuestionSubmit(QuestionId), "the user can correct it and send again");
    }

    [TestMethod]
    public async Task ASendFailure_ReopensTheForm()
    {
        await _frontend.DisplayReplyAsync(FormReply());
        _sut.TryBeginQuestionSubmit(QuestionId);

        _sut.FailQuestionSubmit(QuestionId, "the answer couldn't be sent (bus down)");

        Assert.IsFalse(_sut.IsProcessing);
        Assert.AreEqual(McpQuestionState.Rejected, Question.QuestionState);
        Assert.IsTrue(Question.IsQuestionOpen(DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task AnExpiredForm_IsClosedLocally()
    {
        await _frontend.DisplayReplyAsync(FormReply(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.IsFalse(Question.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.IsFalse(_sut.TryBeginQuestionSubmit(QuestionId));
    }

    [TestMethod]
    public async Task AnOpenForm_SurvivesAReload_InItsPlace()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _frontend.DisplayReplyAsync(FormReply());
        await _frontend.DisplayReplyAsync(FormReply("q_closed"));
        await _frontend.OnMcpQuestionStatusAsync(Status(McpQuestionState.Declined) with { QuestionId = "q_closed" });

        _sut.LoadHistory(
        [
            new ConversationHistoryTurn { Role = "user", Content = "search my mail", Timestamp = before },
            new ConversationHistoryTurn { Role = "assistant", Content = "Fill in the form, please.", Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) },
        ], "blazor-session");

        var messages = _sut.Messages;
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("search my mail", messages[0].Content);
        Assert.AreEqual(QuestionId, messages[1].Question?.QuestionId, "the open form keeps its place in time");
        Assert.AreEqual("Fill in the form, please.", messages[2].Content);
    }
}
