using System.Text;
using A2A;
using Microsoft.AspNetCore.Http;

using A2ATaskStatus = A2A.TaskStatus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.A2A.Gateway.Tests;

[TestClass]
public class SseWriterTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("Test");

    [TestMethod]
    public async Task WriteStream_SetsCorrectHeaders()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await SseWriter.WriteStreamAsync(
            context.Response,
            "1",
            EmptyStream(),
            TestLogger,
            CancellationToken.None);

        Assert.AreEqual("text/event-stream", context.Response.ContentType);
        Assert.AreEqual("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public async Task WriteStream_EmptyStream_WritesNothing()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await SseWriter.WriteStreamAsync(
            context.Response,
            "1",
            EmptyStream(),
            TestLogger,
            CancellationToken.None);

        Assert.AreEqual(0, body.Length);
    }

    [TestMethod]
    public async Task WriteStream_SingleEvent_CorrectSseFormat()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var events = SingleEvent(new StreamResponse
        {
            StatusUpdate = new TaskStatusUpdateEvent
            {
                TaskId = "t1",
                Status = new A2ATaskStatus { State = TaskState.Working }
            }
        });

        await SseWriter.WriteStreamAsync(
            context.Response,
            "42",
            events,
            TestLogger,
            CancellationToken.None);

        body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();

        // Must start with "data: " and end with double newline
        Assert.IsTrue(output.StartsWith("data: "), $"Expected SSE data prefix, got: {output}");
        Assert.IsTrue(output.EndsWith("\n\n"), $"Expected double newline, got: {output}");

        // Must contain JSON-RPC envelope with our ID
        Assert.IsTrue(output.Contains("\"jsonrpc\":\"2.0\""), $"Missing jsonrpc field: {output}");
        Assert.IsTrue(output.Contains("\"id\":42"), $"Missing id field: {output}");
        Assert.IsTrue(output.Contains("\"result\":"), $"Missing result field: {output}");
    }

    [TestMethod]
    public async Task WriteStream_MultipleEvents_EachOnSeparateLine()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var events = MultipleEvents(
            new StreamResponse { Message = new Message { Role = Role.Agent, Parts = [new Part { Text = "one" }] } },
            new StreamResponse { Message = new Message { Role = Role.Agent, Parts = [new Part { Text = "two" }] } }
        );

        await SseWriter.WriteStreamAsync(
            context.Response,
            "1",
            events,
            TestLogger,
            CancellationToken.None);

        body.Seek(0, SeekOrigin.Begin);
        var output = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();

        // Count SSE events (split by double newline)
        var eventCount = output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.AreEqual(2, eventCount, $"Expected 2 events, got output: {output}");
    }

    [TestMethod]
    public async Task WriteStream_Cancellation_HandledGracefully()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Should not throw
        await SseWriter.WriteStreamAsync(
            context.Response,
            "1",
            NeverEndingStream(),
            TestLogger,
            cts.Token);
    }

    private static async IAsyncEnumerable<StreamResponse> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<StreamResponse> SingleEvent(StreamResponse evt)
    {
        await Task.CompletedTask;
        yield return evt;
    }

    private static async IAsyncEnumerable<StreamResponse> MultipleEvents(params StreamResponse[] events)
    {
        await Task.CompletedTask;
        foreach (var evt in events)
            yield return evt;
    }

    private static async IAsyncEnumerable<StreamResponse> NeverEndingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return new StreamResponse
            {
                StatusUpdate = new TaskStatusUpdateEvent
                {
                    TaskId = "t1",
                    Status = new A2ATaskStatus { State = TaskState.Working }
                }
            };
            await Task.Delay(100, ct);
        }
    }
}
