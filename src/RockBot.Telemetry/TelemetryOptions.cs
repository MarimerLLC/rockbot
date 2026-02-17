namespace RockBot.Telemetry;

/// <summary>
/// Configuration options for OpenTelemetry export.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// OTLP exporter endpoint. Defaults to "http://localhost:4317".
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Logical service name reported in traces and metrics.
    /// </summary>
    public string ServiceName { get; set; } = "rockbot";

    /// <summary>
    /// Whether to enable distributed tracing export. Defaults to true.
    /// </summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>
    /// Whether to enable metrics export. Defaults to true.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
}
