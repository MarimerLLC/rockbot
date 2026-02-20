using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RockBot.Telemetry;

/// <summary>
/// DI registration extensions for OpenTelemetry observability.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// All instrumented ActivitySource and Meter names used by RockBot.
    /// </summary>
    private static readonly string[] SourceNames =
    [
        "RockBot.Messaging.RabbitMQ",
        "RockBot.Host",
        "RockBot.Llm",
        "RockBot.Tools"
    ];

    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with OTLP export,
    /// subscribing to all RockBot instrumentation sources.
    /// </summary>
    public static IServiceCollection AddRockBotTelemetry(
        this IServiceCollection services,
        Action<TelemetryOptions>? configure = null)
    {
        var options = new TelemetryOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        var otel = services.AddOpenTelemetry();

        otel.ConfigureResource(resource =>
            resource.AddService(serviceName: options.ServiceName));

        if (options.EnableTracing)
        {
            otel.WithTracing(tracing =>
            {
                foreach (var name in SourceNames)
                    tracing.AddSource(name);

                tracing.AddOtlpExporter(otlp =>
                    otlp.Endpoint = new Uri(options.OtlpEndpoint));
            });
        }

        if (options.EnableMetrics)
        {
            otel.WithMetrics(metrics =>
            {
                foreach (var name in SourceNames)
                    metrics.AddMeter(name);

                metrics.AddOtlpExporter(otlp =>
                    otlp.Endpoint = new Uri(options.OtlpEndpoint));
            });
        }

        return services;
    }
}
