using System.Diagnostics;
using System.Diagnostics.Metrics;
using RockBot.Host;
using RockBot.Host.Middleware;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class TracingMiddlewareTests
{
    private readonly TracingMiddleware _middleware = new();

    private static MessageHandlerContext CreateContext(
        string messageType = "test.message",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var envelope = MessageEnvelope.Create(
            messageType: messageType,
            body: new byte[] { 1 },
            source: "test-source",
            correlationId: "test-corr",
            headers: headers);

        return new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("test-agent"),
            Services = null!, // Not used by tracing middleware
            CancellationToken = CancellationToken.None
        };
    }

    [TestMethod]
    public async Task InvokeAsync_CreatesSpanWithTags()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RockBot.Host",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var context = CreateContext("test.order");
        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.IsNotNull(captured);
        Assert.AreEqual("dispatch test.order", captured.OperationName);
        Assert.AreEqual("test.order", captured.GetTagItem("rockbot.message_type"));
        Assert.AreEqual("test-agent", captured.GetTagItem("rockbot.agent"));
        Assert.IsNotNull(captured.GetTagItem("messaging.message_id"));
        Assert.AreEqual("test-corr", captured.GetTagItem("rockbot.correlation_id"));
    }

    [TestMethod]
    public async Task InvokeAsync_SetsAckResult()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RockBot.Host",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var context = CreateContext();
        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.IsNotNull(captured);
        Assert.AreEqual("ack", captured.GetTagItem("rockbot.result"));
    }

    [TestMethod]
    public async Task InvokeAsync_DeadLetter_SetsErrorStatus()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RockBot.Host",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var context = CreateContext();
        await _middleware.InvokeAsync(context, ctx =>
        {
            ctx.Result = MessageResult.DeadLetter;
            return Task.CompletedTask;
        });

        Assert.IsNotNull(captured);
        Assert.AreEqual(ActivityStatusCode.Error, captured.Status);
        Assert.AreEqual("dead_letter", captured.GetTagItem("rockbot.result"));
    }

    [TestMethod]
    public async Task InvokeAsync_Exception_SetsErrorStatusAndRethrows()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RockBot.Host",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var context = CreateContext();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            _middleware.InvokeAsync(context, _ => throw new InvalidOperationException("boom")));

        Assert.IsNotNull(captured);
        Assert.AreEqual(ActivityStatusCode.Error, captured.Status);
        Assert.AreEqual("error", captured.GetTagItem("rockbot.result"));
    }

    [TestMethod]
    public async Task InvokeAsync_RecordsDispatchDuration()
    {
        var measurements = new List<double>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "rockbot.pipeline.dispatch.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            measurements.Add(value));
        meterListener.Start();

        var context = CreateContext();
        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        meterListener.RecordObservableInstruments();
        Assert.AreEqual(1, measurements.Count);
        Assert.IsTrue(measurements[0] >= 0);
    }

    [TestMethod]
    public async Task InvokeAsync_WithParentContext_LinksToParent()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "RockBot.Host",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        };

        var context = CreateContext(headers: headers);
        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.IsNotNull(captured);
        // The activity should share the parent's trace ID
        Assert.AreEqual(
            ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c"),
            captured.TraceId);
    }

    [TestMethod]
    public async Task InvokeAsync_CallsNext()
    {
        var called = false;
        var context = CreateContext();

        await _middleware.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(called);
    }
}
