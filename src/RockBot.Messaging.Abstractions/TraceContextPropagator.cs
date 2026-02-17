using System.Diagnostics;

namespace RockBot.Messaging;

/// <summary>
/// W3C TraceContext inject/extract utility using only System.Diagnostics.
/// No OpenTelemetry SDK dependency required.
/// </summary>
public static class TraceContextPropagator
{
    private const string TraceparentKey = "traceparent";
    private const string TracestateKey = "tracestate";

    /// <summary>
    /// Injects the current activity's trace context into the given headers dictionary.
    /// Writes "traceparent" and optionally "tracestate" keys.
    /// </summary>
    public static void Inject(Activity? activity, IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (activity is null)
            return;

        // W3C traceparent: {version}-{traceId}-{spanId}-{traceFlags}
        var traceparent = $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}";
        headers[TraceparentKey] = traceparent;

        if (!string.IsNullOrEmpty(activity.TraceStateString))
            headers[TracestateKey] = activity.TraceStateString;
    }

    /// <summary>
    /// Extracts an ActivityContext from the given headers dictionary.
    /// Reads "traceparent" and optionally "tracestate" keys.
    /// Returns null if no valid trace context is found.
    /// </summary>
    public static ActivityContext? Extract(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue(TraceparentKey, out var traceparent))
            return null;

        if (!TryParseTraceparent(traceparent, out var traceId, out var spanId, out var traceFlags))
            return null;

        headers.TryGetValue(TracestateKey, out var tracestate);

        return new ActivityContext(traceId, spanId, traceFlags, tracestate, isRemote: true);
    }

    private static bool TryParseTraceparent(
        string traceparent,
        out ActivityTraceId traceId,
        out ActivitySpanId spanId,
        out ActivityTraceFlags traceFlags)
    {
        traceId = default;
        spanId = default;
        traceFlags = ActivityTraceFlags.None;

        // Format: {version}-{traceId}-{spanId}-{traceFlags}
        // Example: 00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01
        var parts = traceparent.Split('-');
        if (parts.Length < 4)
            return false;

        // Version must be "00"
        if (parts[0] != "00")
            return false;

        // TraceId must be 32 hex chars
        if (parts[1].Length != 32)
            return false;

        // SpanId must be 16 hex chars
        if (parts[2].Length != 16)
            return false;

        // TraceFlags must be 2 hex chars
        if (parts[3].Length != 2)
            return false;

        try
        {
            traceId = ActivityTraceId.CreateFromString(parts[1]);
            spanId = ActivitySpanId.CreateFromString(parts[2]);
            traceFlags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
