using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.UserProxy;

/// <summary>
/// Centralized diagnostics instrumentation for the user proxy layer.
/// </summary>
internal static class UserProxyDiagnostics
{
    public const string ActivitySourceName = "RockBot.UserProxy";
    public const string MeterName = "RockBot.UserProxy";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagesSent =
        Meter.CreateCounter<long>(
            "rockbot.userproxy.messages_sent",
            unit: "{message}",
            description: "Total number of user messages sent");

    public static readonly Counter<long> RepliesReceived =
        Meter.CreateCounter<long>(
            "rockbot.userproxy.replies_received",
            unit: "{reply}",
            description: "Total number of agent replies received");

    public static readonly Histogram<double> RoundtripDuration =
        Meter.CreateHistogram<double>(
            "rockbot.userproxy.roundtrip.duration",
            unit: "ms",
            description: "Duration of user message round-trip (send to reply)");
}
