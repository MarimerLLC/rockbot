using System.Text.Json;
using System.Text.Json.Nodes;
using RockBot.AgUi.DevHost;
using RockBot.UserProxy;
using RockBot.UserProxy.ChatState;

namespace RockBot.AgUi.Client.Tests;

/// <summary>
/// The client transport against the real proxy: <c>RockBot.UserProxy.AgUi</c>'s own HTTP pipeline
/// (through the dev host), the in-process bus, and the scripted demo agent. Nothing between the
/// client's <see cref="HttpClient"/> and the bus is faked.
/// </summary>
[TestClass]
public sealed class ChatSessionEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private DevProxy _proxy = null!;
    private HttpClient _http = null!;
    private ChatStateService _chat = null!;
    private ChatSession _session = null!;

    [TestInitialize]
    public async Task Init()
    {
        _proxy = await DevProxy.StartAsync();
        _proxy.Agent.NotifyDelay = TimeSpan.FromMilliseconds(50);
        _http = _proxy.CreateClient();
        (_session, _chat) = Open(_http);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _session.DisposeAsync();
        _http.Dispose();
        await _proxy.DisposeAsync();
    }

    private static (ChatSession Session, ChatStateService Chat) Open(HttpClient http)
    {
        var chat = new ChatStateService();
        var client = new AgUiProxyClient(http, new AgUiProxyClientOptions { BaseAddress = http.BaseAddress! });
        return (new ChatSession(client, chat, new ChatSessionOptions { ReconnectDelays = [TimeSpan.FromMilliseconds(20)] }), chat);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private async Task ConnectAsync()
    {
        _session.Start();
        await UntilAsync(() => _session.Connection == NotifyConnection.Connected && _chat.State.AgentName is not null, "the notify stream");
    }

    [TestMethod]
    public async Task ATurn_ShowsItsProgressToolCallTaskListAndReply()
    {
        await ConnectAsync();

        await _session.SendAsync("Hello there");

        var state = _chat.State;
        Assert.IsFalse(_chat.IsBusy);
        CollectionAssert.AreEqual(
            new[] { MessageCategory.UserInput, MessageCategory.PrimaryProgress, MessageCategory.PrimaryFinal },
            state.Messages.Select(m => m.Category).ToArray());
        Assert.AreEqual("Echo: Hello there", state.Messages[2].Content);
        Assert.AreEqual("RockBot", state.Messages[2].AgentName);

        var call = state.Messages[1].ActivityLogEntries.Single(e => e.ToolCall is not null).ToolCall!;
        Assert.AreEqual(("web_search", ToolCallPhase.Finished, "3 results"), (call.ToolCallName, call.Phase, call.Result));
        Assert.AreEqual("query: Hello there", call.ArgsSummary);

        var plan = state.TaskLists[ReplySourceKind.Primary];
        Assert.AreEqual(2, plan.CompletedCount);

        // What the agent was told about this client.
        var received = _proxy.Agent.Messages.Single();
        Assert.AreEqual("agui-session", received.SessionId);
        Assert.AreEqual("agui", received.ChannelName);
        Assert.IsTrue(received.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms), "custom question mode asks for forms");
        Assert.IsTrue(received.ClientCapabilities.HasFlag(ClientCapabilities.MarkdownBasic));
        Assert.IsFalse(received.ClientCapabilities.HasFlag(ClientCapabilities.HtmlInline));
        Assert.IsFalse(received.ClientCapabilities.HasFlag(ClientCapabilities.SvgInline));
        Assert.IsFalse(received.ClientCapabilities.HasFlag(ClientCapabilities.MarkdownTables), "the sample renders no tables, so it doesn't ask for them");
    }

    [TestMethod]
    public async Task OutputNobodyAskedFor_ArrivesOnTheNotifyStream_WithItsOrigin()
    {
        await ConnectAsync();

        await _session.SendAsync("Hello, and notify me");
        await UntilAsync(() => _chat.State.Messages.Any(m => m.Category == MessageCategory.ScheduledUser), "the scheduled task's report");

        var state = _chat.State;
        var result = state.Messages.Single(m => m.Category == MessageCategory.SubagentActivity && !m.IsActivityLog);
        Assert.AreEqual("Vendor B is the cheapest by 12%.", result.Content);

        var log = state.Messages.Single(m => m.Category == MessageCategory.SubagentActivity && m.IsActivityLog);
        Assert.IsTrue(log.ActivityLogEntries.Any(e => e.Content == "Reading the price list"));
        Assert.IsFalse(state.IsActivityLogOpen(log.MessageId), "the subagent finished");
        Assert.AreEqual(0, state.ActiveStatusIndicators.Count);
        Assert.AreEqual(1, state.TaskLists.Count, "the subagent's plan went with it; the turn's own is still shown");

        var report = state.Messages.Single(m => m.Category == MessageCategory.ScheduledUser);
        StringAssert.Contains(report.Content, "Re: \"Daily summary\"");
        StringAssert.Contains(report.Content, "from scheduler");
        StringAssert.Contains(report.Content, "Nothing needs your attention today.");
        Assert.AreEqual("scheduled", report.SessionId, "the proxy says which session a reply came in");
    }

    [TestMethod]
    public async Task AQuestion_IsShownAsAForm_AnsweredOverRest_AndClosedByTheNotifyStream()
    {
        await ConnectAsync();

        await _session.SendAsync("I have a question for the scheduler");

        var form = _chat.State.Messages.Single(m => m.Category == MessageCategory.McpQuestion);
        var question = form.Question!;
        Assert.AreEqual("Which time zone should the meeting be in?", question.Message);
        Assert.IsTrue(form.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.IsFalse(_chat.IsBusy, "custom mode: the run ended and the user can keep chatting");
        Assert.AreEqual("The scheduler needs a time zone; please fill in the form.", _chat.State.Messages[^1].Content);

        var answers = new JsonObject { ["tz"] = "Europe/Brussels", ["length"] = "60", ["notify"] = true };
        Assert.IsTrue(await _session.AnswerAsync(question.QuestionId, answers));
        await UntilAsync(() => _chat.State.Messages[^1].Content.StartsWith("Booked.", StringComparison.Ordinal), "the agent's reply to the answer");

        var closed = _chat.State.FindQuestion(question.QuestionId)!;
        Assert.AreEqual(McpQuestionState.Answered, closed.QuestionState);
        Assert.AreEqual("user", closed.QuestionAnsweredBy);
        Assert.IsFalse(_chat.IsBusy);

        var answer = _proxy.Agent.Answers.Single();
        Assert.IsFalse(answer.Decline);
        using var sent = JsonDocument.Parse(answer.Answers!);
        Assert.AreEqual("Europe/Brussels", sent.RootElement.GetProperty("tz").GetString());
        Assert.AreEqual("60", sent.RootElement.GetProperty("length").GetString());
        Assert.IsTrue(sent.RootElement.GetProperty("notify").GetBoolean());
    }

    [TestMethod]
    public async Task AQuestion_CanBeDeclined()
    {
        await ConnectAsync();
        await _session.SendAsync("question");
        var id = _chat.State.Messages.Single(m => m.Category == MessageCategory.McpQuestion).Question!.QuestionId;

        Assert.IsTrue(await _session.AnswerAsync(id, answers: null, decline: true));
        await UntilAsync(() => _chat.State.Messages[^1].Content == "Okay, I won't book it.", "the agent's reply to the decline");

        Assert.AreEqual(McpQuestionState.Declined, _chat.State.FindQuestion(id)!.QuestionState);
        Assert.IsTrue(_proxy.Agent.Answers.Single().Decline);
    }

    [TestMethod]
    public async Task AClientThatStartsLater_LoadsTheConversationAndTheQuestionStillOpen()
    {
        // An earlier client's session: a turn, and a question nobody answered.
        await ConnectAsync();
        await _session.SendAsync("first, a question");
        var id = _chat.State.Messages.Single(m => m.Category == MessageCategory.McpQuestion).Question!.QuestionId;

        using var http = _proxy.CreateClient();
        var (later, chat) = Open(http);
        await using var disposing = later;
        later.Start();
        await UntilAsync(() => chat.State.FindQuestion(id) is not null, "the open question, from GET /threads/{id}/questions");

        CollectionAssert.AreEqual(
            new[] { "first, a question", "The scheduler needs a time zone; please fill in the form." },
            chat.State.Messages.Where(m => m.Category != MessageCategory.McpQuestion).Select(m => m.Content).ToArray());
        Assert.IsTrue(chat.State.Messages[0].IsFromUser);
        Assert.IsTrue(chat.State.FindQuestion(id)!.IsQuestionOpen(DateTimeOffset.UtcNow));
        Assert.AreEqual("demo", chat.State.AgentVersion);

        // Either client can answer; both see it close.
        Assert.IsTrue(await later.AnswerAsync(id, new JsonObject { ["tz"] = "UTC" }));
        await UntilAsync(() => _chat.State.FindQuestion(id)!.QuestionState == McpQuestionState.Answered, "the first client's form closing");
        await UntilAsync(() => chat.State.FindQuestion(id)!.QuestionState == McpQuestionState.Answered, "the second client's form closing");
    }

    [TestMethod]
    public async Task AnUploadedFile_GoesWithTheMessage_AndCanBeFetchedBack()
    {
        await ConnectAsync();
        var bytes = DemoAgent.DemoImage(8);

        var attachment = await _session.UploadAsync("screenshot.png", "image/png", bytes);
        await _session.SendAsync("What is this?", [attachment]);

        Assert.AreEqual("image/png", attachment.Mime);
        Assert.AreEqual("screenshot.png", attachment.FileName);
        var received = _proxy.Agent.Messages.Single();
        Assert.AreEqual(attachment.Path, received.Attachments!.Single().Path);
        StringAssert.Contains(_chat.State.Messages[^1].Content, "I got 1 file(s): screenshot.png");
        Assert.AreEqual(attachment.Path, _chat.State.Messages[0].Attachments!.Single().Path, "the user's message shows what was attached");

        var url = await _session.AttachmentUrlAsync(attachment);
        using var fetched = await _http.GetAsync(url);
        Assert.AreEqual("image/png", fetched.Content.Headers.ContentType!.MediaType);
        CollectionAssert.AreEqual(bytes, await fetched.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task AnUploadTheAgentRefuses_SaysWhy()
    {
        await ConnectAsync();

        var error = await Assert.ThrowsAsync<AgUiProxyException>(
            () => _session.UploadAsync("notes.txt", "text/plain", new byte[] { 1, 2, 3 }));

        Assert.AreEqual(System.Net.HttpStatusCode.UnsupportedMediaType, error.Status);
        StringAssert.Contains(error.Message, ".txt");
    }

    [TestMethod]
    public async Task ARepliesImage_HasAUrlTheBrowserCanLoad()
    {
        await ConnectAsync();

        await _session.SendAsync("an image please");

        var image = _chat.State.Messages[^1].Attachments!.Single();
        Assert.AreEqual("image/png", image.Mime);
        var url = await _session.AttachmentUrlAsync(image);
        StringAssert.StartsWith(url.AbsolutePath, "/attachments/demo-");
        using var fetched = await _http.GetAsync(url);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, fetched.StatusCode);
        Assert.AreEqual("image/png", fetched.Content.Headers.ContentType!.MediaType);
    }

    [TestMethod]
    public async Task Cancel_StopsTheAgentsTurn_AndTheChatIsNoLongerBusy()
    {
        await ConnectAsync();

        var turn = _session.SendAsync("this will be slow");
        await UntilAsync(() => _chat.State.ThinkingMessage?.StartsWith("Still thinking", StringComparison.Ordinal) == true, "the agent's heartbeat");
        Assert.IsTrue(_chat.IsBusy);

        await _session.CancelAsync();
        await turn.WaitAsync(Patience);

        Assert.IsFalse(_chat.IsBusy);
        Assert.IsFalse(_chat.State.Messages.Any(m => m.Content == "That took a while."), "the agent stopped");
    }

    [TestMethod]
    public async Task ATurnTheAgentNeverStarts_EndsAsATimeout_AndTheUserCanSendAgain()
    {
        await using var proxy = await DevProxy.StartAsync(new DevProxyOptions
        {
            Settings = new Dictionary<string, string> { ["UserProxy:ReplyTimeout"] = "00:00:01" },
        });
        using var http = proxy.CreateClient();
        var (session, chat) = Open(http);
        await using var disposing = session;

        await session.SendAsync("silent");

        Assert.IsFalse(chat.IsBusy);
        Assert.AreEqual("No reply received (timeout)", chat.State.Messages[^1].Content);

        await session.SendAsync("Hello again");
        Assert.AreEqual("Echo: Hello again", chat.State.Messages[^1].Content);
    }

    [TestMethod]
    public async Task ClearingTheContext_EmptiesEveryClientsTranscript()
    {
        await ConnectAsync();
        await _session.SendAsync("Hello");

        using var response = await _http.PostAsync("/threads/agui-session/clear", content: null);
        await UntilAsync(() => _chat.State.Messages is [{ Content: "Context cleared — starting fresh." }], "the cleared transcript");

        Assert.AreEqual(System.Net.HttpStatusCode.Accepted, response.StatusCode);
    }
}
