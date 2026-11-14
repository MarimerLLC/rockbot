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
    public async Task ANotifyClientThatStopsReading_IsDisconnected_AndHoldsNobodyUp()
    {
        // A small buffer and queue, and events big enough to fill the socket's buffers quickly.
        await using var host = new AgUiTestHost(("AgUi:NotifyBufferSize", "4"), ("AgUi:NotifySubscriberQueueSize", "8"));
        host.UseKestrel(0);
        host.StartServer();
        using var client = host.CreateClient();
        await using var agent = await FakeAgent.StartAsync(host);

        using var stuck = await NotifyStream.OpenAsync(client);
        using var reading = await NotifyStream.OpenAsync(client);
        var lastSeen = (await stuck.ReadConnectedAsync()).Id;
        await reading.ReadConnectedAsync();

        // The stuck client reads nothing from here on. The other one gets every event, promptly:
        // the bus's thread is never waiting on the stuck client's socket.
        const int count = 80;
        var padding = new string('x', 256 * 1024);
        for (var i = 1; i <= count; i++)
        {
            await agent.BroadcastAsync(new AgentNameChanged { AgentName = $"{i}:{padding}" });
            var frame = await reading.ReadAsync();
            Assert.AreEqual(i, frame.Sequence);
        }

        // The proxy gave up on the stuck client: its stream ends, short of the events it missed.
        var received = 0;
        try
        {
            while (await stuck.NextAsync() is { } frame)
            {
                lastSeen = frame.Id;
                received++;
            }
        }
        catch (IOException)
        {
            // How the end looks from here: the proxy stopped mid-write, so the connection is
            // reset rather than closed cleanly, and what was still in flight is lost.
        }
        Assert.IsTrue(received < count, $"the stuck client got {received} of {count} events before it was dropped");

        // It reconnects with the last id it got. It missed more than the buffer holds, so it is
        // told to resync; a smaller gap would have been replayed.
        using var again = await NotifyStream.OpenAsync(client, lastEventId: lastSeen);
        var first = await again.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, first.Name);
        Assert.AreEqual(count, first.Sequence);
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

    /// <summary>A body whose length the client doesn't announce, so only reading it shows how long it is.</summary>
    private sealed class ChunkedContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            for (var sent = 0L; sent < length; sent += chunk.Length)
                await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [TestMethod]
    public async Task AnUploadThatHidesItsLength_IsCutOffAtTheLimit()
    {
        // Without a Content-Length there is nothing to refuse up front: the server itself has to
        // stop reading at the limit, which only a real server does.
        _agent.OnUpload = request => new AttachmentUploadResponse
        {
            Success = true, Attachment = new AgentAttachment { Mime = request.Mime, Path = request.FileName },
        };
        var content = new ChunkedContent(AttachmentUploadRequest.MaxBytes * 3);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        try
        {
            using var response = await _client.PostAsync("/attachments?fileName=big.png", content).WaitAsync(Patience);
            Assert.AreEqual(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // The server answered 413 and closed the connection while the client was still
            // sending; depending on timing the client sees the reset before the answer.
        }

        await Task.Delay(200);
        Assert.AreEqual(0, _agent.Uploads.Count, "the agent was handed a file over the limit");
    }
}