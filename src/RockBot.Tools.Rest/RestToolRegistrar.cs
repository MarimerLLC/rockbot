using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Rest;

/// <summary>
/// Registers REST endpoints as tools in the tool registry on startup.
/// </summary>
internal sealed class RestToolRegistrar(
    IToolRegistry registry,
    RestToolOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<RestToolRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in options.Endpoints)
        {
            var executor = new RestToolExecutor(endpoint, httpClientFactory);
            var registration = new ToolRegistration
            {
                Name = endpoint.Name,
                Description = endpoint.Description,
                ParametersSchema = endpoint.ParametersSchema,
                Source = "rest"
            };

            registry.Register(registration, executor);
            logger.LogInformation("Registered REST tool: {ToolName} → {Method} {Url}",
                endpoint.Name, endpoint.Method, endpoint.UrlTemplate);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
