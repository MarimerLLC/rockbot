using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>Prints the agent's name and version (round-trip ping).</summary>
internal sealed class InfoCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var info = await proxy.GetAgentInfoAsync();
            if (info is null)
            {
                Console.Error.WriteLine("error: agent did not respond before timeout");
                return 2;
            }

            Console.Out.WriteLine($"{info.AgentName} {info.AgentVersion}");
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
