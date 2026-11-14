using AGUI.Client;
using Microsoft.Extensions.AI;

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
}
