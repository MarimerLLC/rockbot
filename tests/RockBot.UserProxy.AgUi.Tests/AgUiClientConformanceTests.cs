using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// Conformance: the AG-UI SDK's own client (<see cref="AGUIChatClient"/>) drives <c>POST /agui</c>,
/// so its request shape is one the proxy accepts and its parser accepts the stream the proxy emits.
/// </summary>
[TestClass]
public sealed class AgUiClientConformanceTests
{
    private const string Thread = "agui-session";

    private AgUiTestHost _host = null!;
    private HttpClient _http = null!;
    private FakeAgent _agent = null!;
    private AGUIChatClient _chat = null!;

    [TestInitialize]
    public async Task Init()
    {
        _host = new AgUiTestHost();
        _http = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
        _chat = new AGUIChatClient(new AGUIChatClientOptions(_http, new Uri(_http.BaseAddress!, "agui").ToString()));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _chat.Dispose();
        await _agent.DisposeAsync();
        _http.Dispose();
        await _host.DisposeAsync();
    }

    [TestMethod]
    public async Task ChatClient_GetsTheAgentsReply()
    {
        var response = await _chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello from the SDK")],
            new ChatOptions { ConversationId = Thread });

        StringAssert.Contains(response.Text, "Echo: Hello from the SDK");

        var received = _agent.Messages.Single();
        Assert.AreEqual("Hello from the SDK", received.Message.Content);
        Assert.AreEqual(Thread, received.Message.SessionId);
    }

    /// <summary>
    /// App mode (#794, phase 5): the SDK's client has no auth of its own; it sends whatever its
    /// <see cref="HttpClient"/> does, so a bearer token on that is all it takes.
    /// </summary>
    [TestMethod]
    public async Task ChatClient_WithABearerToken_RunsATurnAgainstAProxyInAppMode()
    {
        using var issuer = new TestIssuer();
        await using var host = new AgUiTestHost(TestIssuer.Settings()) { ConfigureServices = issuer.Register };
        using var http = host.CreateClient();
        await using var agent = await FakeAgent.StartAsync(host);
        var endpoint = new Uri(http.BaseAddress!, "agui").ToString();

        // Without the token the client fails, and nothing reaches the agent.
        using (var anonymous = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint)))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Hello")], new ChatOptions { ConversationId = Thread }));
            Assert.AreEqual(0, agent.Messages.Count);
        }

        http.DefaultRequestHeaders.Authorization = issuer.Bearer();
        using var chat = new AGUIChatClient(new AGUIChatClientOptions(http, endpoint));
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello with a token")], new ChatOptions { ConversationId = Thread });

        StringAssert.Contains(response.Text, "Echo: Hello with a token");
        Assert.AreEqual(TestIssuer.AllowedEmail, agent.Messages.Single().Message.UserId);
    }

    [TestMethod]
    public async Task ChatClient_ReadsARunWithProgressToolCallsAndState()
    {
        _agent.OnMessage = async (agent, received) =>
        {
            var call = new ToolCallInfo
            {
                ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started, ArgsSummary = "query: rockbot",
            };
            await agent.RunStartedAsync(received);
            await agent.ReplyAsync(received, "Thinking", isFinal: false);
            await agent.ReplyAsync(received, "Calling web_search…", isFinal: false, toolCall: call);
            await agent.ReplyAsync(received, "done", isFinal: false,
                toolCall: call with { Phase = ToolCallPhase.Finished, Result = "3 results", DurationMs = 120 });
            await agent.SendAsync(received, new TaskListSnapshot
            {
                ThreadId = Thread, Source = ReplySource.Primary,
                Items = [new TaskListItem { Id = "1", Description = "search", Status = "completed" }],
            });
            await agent.ReplyAsync(received, "Found it.");
            await agent.RunFinishedAsync(received, RunOutcome.Success);
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Search")], new ChatOptions { ConversationId = Thread }))
        {
            updates.Add(update);
        }

        StringAssert.Contains(string.Concat(updates.Select(u => u.Text)), "Found it.");
    }

    // ── MCP questions (#794, phase 3) ────────────────────────────────────────

    private static readonly McpQuestionForm Question = new()
    {
        QuestionId = "q1", ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        Fields = [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true }],
    };

    /// <summary>
    /// Chat options for the thread with <c>forwardedProps.rockbot.questionMode</c> set, the way
    /// the SDK's client lets a caller add to the request.
    /// </summary>
    private static ChatOptions WithQuestionMode(string mode) => new()
    {
        ConversationId = Thread,
        RawRepresentationFactory = _ => new RunAgentInput
        {
            ForwardedProperties = JsonSerializer.SerializeToElement(new { rockbot = new { questionMode = mode } }),
        },
    };

    /// <summary>The agent's turn hands back a question as a form, then says so.</summary>
    private void AgentAsksDuringTheTurn() => _agent.OnMessage = async (agent, received) =>
    {
        await agent.FormAsync(Question);
        var registry = _host.Services.GetRequiredService<McpQuestionRegistry>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (registry.Find(Question.QuestionId) is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await agent.ReplyAsync(received, "Please fill in the form.");
    };

    [TestMethod]
    public async Task ChatClient_ReadsAnInterrupt_AndResumesIt()
    {
        AgentAsksDuringTheTurn();
        _agent.OnAnswer = (agent, answer) => agent.AcceptAsync(answer, "Booked for Brussels time.");
        var options = WithQuestionMode("interrupt");

        // The run ends with an interrupt, which the client hands over as content to answer.
        var first = await _chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Book it")], options);

        StringAssert.Contains(first.Text, "Please fill in the form.");
        var request = first.Messages.SelectMany(m => m.Contents).OfType<InterruptRequestContent>().Single();
        Assert.AreEqual("q1", request.RequestId);
        Assert.AreEqual(InterruptReasons.InputRequired, request.Reason);
        Assert.AreEqual("Which time zone?", request.Message);
        Assert.IsTrue(DateTimeOffset.TryParse(request.ExpiresAt, out _), request.ExpiresAt);
        Assert.AreEqual("string", request.ResponseSchema!.Value.GetProperty("properties").GetProperty("tz").GetProperty("type").GetString());

        // Its answer goes back as the next run's resume, and that run streams the agent's reply.
        var messages = new List<ChatMessage> { new(ChatRole.User, "Book it") };
        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.User,
            [new InterruptResponseContent(request.RequestId) { Payload = JsonSerializer.SerializeToElement(new { tz = "Europe/Brussels" }) }]));
        var resumed = await _chat.GetResponseAsync(messages, options);

        StringAssert.Contains(resumed.Text, "Booked for Brussels time.");
        Assert.IsFalse(resumed.Messages.SelectMany(m => m.Contents).OfType<InterruptRequestContent>().Any());

        var answer = _agent.Answers.Single();
        Assert.AreEqual("q1", answer.QuestionId);
        Assert.IsFalse(answer.Decline);
        Assert.AreEqual("Europe/Brussels", JsonDocument.Parse(answer.Answers!).RootElement.GetProperty("tz").GetString());
        Assert.AreEqual(1, _agent.Messages.Count, "the resume replayed the conversation, and none of it was sent again");
    }

    [TestMethod]
    public async Task ChatClient_IsToldWhenNewInputIsBlockedByAnInterrupt()
    {
        AgentAsksDuringTheTurn();
        var options = WithQuestionMode("interrupt");
        await _chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Book it")], options);

        // RUN_ERROR, with rockbot.mcp_question events before it: the client's parser takes the
        // stream, and reports the error its own way.
        Exception? error = null;
        ChatResponse? response = null;
        try
        {
            response = await _chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Something else")], options);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Assert.AreEqual(1, _agent.Messages.Count, "the blocked input never reached the agent");
        var said = error?.ToString()
            ?? string.Join(" ", response!.Messages.SelectMany(m => m.Contents).Select(c => c is ErrorContent e ? $"{e.ErrorCode} {e.Message}" : c.ToString()));
        StringAssert.Contains(said, "interrupts");
    }

    [TestMethod]
    public async Task ChatClient_ReadsARunWithACustomModeQuestion()
    {
        AgentAsksDuringTheTurn();

        // A CUSTOM rockbot.mcp_question event mid-run, and a plain RUN_FINISHED.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Book it")], WithQuestionMode("custom")))
        {
            updates.Add(update);
        }

        StringAssert.Contains(string.Concat(updates.Select(u => u.Text)), "Please fill in the form.");
        Assert.IsFalse(updates.SelectMany(u => u.Contents).OfType<InterruptRequestContent>().Any());
    }}
