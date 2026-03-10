namespace RockBot.Telemetry;

/// <summary>
/// Configuration options for OpenTelemetry export.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// Enables OpenTelemetry export. When false, no OTel providers are registered.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// OTLP exporter endpoint. Defaults to "http://localhost:4317".
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Logical service name reported in traces, metrics, and logs.
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

    /// <summary>
    /// Whether to enable log export via OTLP (routes to Loki via Grafana Alloy). Defaults to true.
    /// </summary>
    public bool EnableLogging { get; set; } = true;
}
