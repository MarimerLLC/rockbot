using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Hosted service that registers the <c>get_dream_service_cost</c> tool with the tool
/// registry at startup. Mirrors <c>SchedulingToolRegistrar</c>'s shape (RockBot.Tools.Scheduling)
/// but lives in RockBot.Host, next to <see cref="DreamService"/> and <see cref="DreamCostTracker"/>.
/// </summary>
internal sealed class DreamCostToolRegistrar(
    IToolRegistry registry,
    IOptions<AgentProfileOptions> profileOptions,
    ILoggerFactory loggerFactory,
    ILogger<DreamCostToolRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = DreamService.ResolvePath(DreamCostTracker.FileName, profileOptions.Value.BasePath);

        registry.Register(new ToolRegistration
        {
            Name = "get_dream_service_cost",
            Description = "Report the cumulative LLM token usage and estimated USD cost DreamService's " +
                "background memory/skill-consolidation cycles have run up, plus the most recent cycle's " +
                "figures. DreamService runs on its own schedule and is not itself a scheduled task, so " +
                "this is the only place its spend is visible.",
            ParametersSchema = null,
            Source = "dream"
        }, new GetDreamServiceCostExecutor(path, loggerFactory.CreateLogger<DreamCostTracker>()));
        logger.LogInformation("Registered dream tool: get_dream_service_cost");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
