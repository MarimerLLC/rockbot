using System.Diagnostics;
using RockBot.Messaging;

namespace RockBot.Messaging.Tests;

[TestClass]
public class TraceContextPropagatorTests
{
    [TestMethod]
    public void InjectExtract_RoundTrips()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("test");
        using var activity = source.StartActivity("test-op", ActivityKind.Producer);
        Assert.IsNotNull(activity);

        var headers = new Dictionary<string, string>();
        TraceContextPropagator.Inject(activity, headers);

        Assert.IsTrue(headers.ContainsKey("traceparent"));

        var extracted = TraceContextPropagator.Extract(headers);
        Assert.IsNotNull(extracted);
        Assert.AreEqual(activity.TraceId, extracted.Value.TraceId);
        Assert.AreEqual(activity.SpanId, extracted.Value.SpanId);
        Assert.IsTrue(extracted.Value.IsRemote);
    }

    [TestMethod]
    public void Inject_NullActivity_NoHeaders()
    {
        var headers = new Dictionary<string, string>();
        TraceContextPropagator.Inject(null, headers);

        Assert.AreEqual(0, headers.Count);
    }

    [TestMethod]
    public void Inject_NullHeaders_ThrowsArgumentNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            TraceContextPropagator.Inject(null, null!));
    }

    [TestMethod]
    public void Extract_NullHeaders_ThrowsArgumentNull()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            TraceContextPropagator.Extract(null!));
    }

    [TestMethod]
    public void Extract_NoTraceparent_ReturnsNull()
    {
        var headers = new Dictionary<string, string>();
        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Extract_InvalidTraceparent_ReturnsNull()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "invalid-data"
        };
        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Extract_WrongVersion_ReturnsNull()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "ff-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        };
        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Extract_ShortTraceId_ReturnsNull()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af765-b7ad6b7169203331-01"
        };
        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Extract_ValidTraceparent_ParsesCorrectly()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        };

        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNotNull(result);
        Assert.AreEqual(ActivityTraceId.CreateFromString("0af7651916cd43dd8448eb211c80319c"), result.Value.TraceId);
        Assert.AreEqual(ActivitySpanId.CreateFromString("b7ad6b7169203331"), result.Value.SpanId);
        Assert.AreEqual(ActivityTraceFlags.Recorded, result.Value.TraceFlags);
        Assert.IsTrue(result.Value.IsRemote);
    }

    [TestMethod]
    public void Extract_UnrecordedFlag_ParsesCorrectly()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00"
        };

        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNotNull(result);
        Assert.AreEqual(ActivityTraceFlags.None, result.Value.TraceFlags);
    }

    [TestMethod]
    public void Extract_WithTracestate_PreservesIt()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["tracestate"] = "congo=t61rcWkgMzE"
        };

        var result = TraceContextPropagator.Extract(headers);

        Assert.IsNotNull(result);
        Assert.AreEqual("congo=t61rcWkgMzE", result.Value.TraceState);
    }

    [TestMethod]
    public void Inject_RecordedActivity_SetsRecordedFlag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("test");
        using var activity = source.StartActivity("test-op");
        Assert.IsNotNull(activity);

        var headers = new Dictionary<string, string>();
        TraceContextPropagator.Inject(activity, headers);

        Assert.IsTrue(headers["traceparent"].EndsWith("-01"));
    }

    [TestMethod]
    public void Inject_WithTraceState_IncludesIt()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("test");
        using var activity = source.StartActivity("test-op");
        Assert.IsNotNull(activity);
        activity.TraceStateString = "vendor=value";

        var headers = new Dictionary<string, string>();
        TraceContextPropagator.Inject(activity, headers);

        Assert.AreEqual("vendor=value", headers["tracestate"]);
    }
}
