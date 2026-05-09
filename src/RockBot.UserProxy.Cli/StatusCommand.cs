using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Prints a snapshot of the agent's current activity: whether it's processing
/// a user message, and any active subagents.
/// </summary>
internal sealed class StatusCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var status = await proxy.GetActiveStatusAsync();
            if (status is null)
            {
                Console.Error.WriteLine("error: agent did not respond before timeout");
                return 2;
            }

            Console.Out.WriteLine($"processing: {status.IsProcessing}");
            Console.Out.WriteLine($"subagents: {status.Subagents.Count}");
            foreach (var s in status.Subagents)
                Console.Out.WriteLine($"  - {s.TaskId} (started {s.StartedAt:O}): {s.Description}");
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
