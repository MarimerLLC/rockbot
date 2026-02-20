using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// Centralized diagnostics instrumentation for the RabbitMQ messaging layer.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class RabbitMqDiagnostics
{
    public const string ActivitySourceName = "RockBot.Messaging.RabbitMQ";
    public const string MeterName = "RockBot.Messaging.RabbitMQ";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> PublishDuration =
        Meter.CreateHistogram<double>(
            "rockbot.messaging.publish.duration",
            unit: "ms",
            description: "Duration of message publish operations");

    public static readonly Counter<long> PublishMessages =
        Meter.CreateCounter<long>(
            "rockbot.messaging.publish.messages",
            unit: "{message}",
            description: "Total number of messages published");

    public static readonly Histogram<double> ProcessDuration =
        Meter.CreateHistogram<double>(
            "rockbot.messaging.process.duration",
            unit: "ms",
            description: "Duration of message processing operations");

    public static readonly Counter<long> ProcessMessages =
        Meter.CreateCounter<long>(
            "rockbot.messaging.process.messages",
            unit: "{message}",
            description: "Total number of messages processed");

    public static readonly UpDownCounter<long> ActiveMessages =
        Meter.CreateUpDownCounter<long>(
            "rockbot.messaging.active_messages",
            unit: "{message}",
            description: "Number of messages currently being processed");
}
