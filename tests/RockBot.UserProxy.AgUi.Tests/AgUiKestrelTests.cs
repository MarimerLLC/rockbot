using System.Text;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The two things an in-memory test server can't show: that events reach a client over a real
/// socket as they happen, not when the run ends, and that closing the connection is seen as an abort.
/// </summary>
[TestClass]
public sealed class AgUiKestrelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;

    [TestInitialize]
    public async Task Init()
    {
        _host = new AgUiTestHost();
        _host.UseKestrel(0);
        _host.StartServer();
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _agent.DisposeAsync();
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private static StringContent Run(string runId) => new(
        $$"""{"threadId":"agui-session","runId":"{{runId}}","messages":[{"id":"m1","role":"user","content":"Hello"}]}""",
        Encoding.UTF8, "application/json");

    private static async Task<string> ReadEventAsync(StreamReader reader)
    {
        var block = new StringBuilder();
        while (await reader.ReadLineAsync().WaitAsync(Patience) is { } line)
        {
            if (line.Length == 0 && block.Length > 0)
                return block.ToString();
            block.Append(line);
        }
        throw new EndOfStreamException("the stream ended before a whole event arrived");
    }

    [TestMethod]
    public async Task Events_ReachTheClientWhileTheRunIsStillOpen()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agent.OnMessage = async (agent, received) =>
        {
            await agent.ReplyAsync(received, "Working on it", isFinal: false);
            await release.Task;
            await agent.ReplyAsync(received, "Done");
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            // Both arrive before the agent has answered.
            StringAssert.Contains(await ReadEventAsync(reader), "RUN_STARTED");
            StringAssert.Contains(await ReadEventAsync(reader), "ACTIVITY_SNAPSHOT");

            release.SetResult();
            var rest = await reader.ReadToEndAsync().WaitAsync(Patience);
            StringAssert.Contains(rest, "Done");
            StringAssert.Contains(rest, "RUN_FINISHED");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ClosingTheConnection_CancelsTheSession()
    {
        _agent.OnMessage = (agent, received) => agent.RunStartedAsync(received);

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") })
        using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync()))
        {
            StringAssert.Contains(await ReadEventAsync(reader), "RUN_STARTED");
            Assert.IsFalse(_agent.CancelRequested.IsCompleted);
        }

        Assert.AreEqual("agui-session", (await _agent.CancelRequested.WaitAsync(Patience)).SessionId);
    }
}
