using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Publishes a CancelSessionRequest, asking the agent to stop in-flight work
/// for the given session. Fire-and-forget.
/// </summary>
internal sealed class CancelCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var sessionId = settings.SessionId ?? "cli-session";
            await proxy.CancelSessionAsync(sessionId);
            Console.Out.WriteLine($"cancel sent for session '{sessionId}'");
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
