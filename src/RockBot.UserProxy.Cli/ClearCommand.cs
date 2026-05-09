using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Publishes a ClearContextRequest, resetting the in-memory conversation
/// context for the given session. Long-term memory and saved logs are not
/// affected. Fire-and-forget — there is no ack to await.
/// </summary>
internal sealed class ClearCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var sessionId = settings.SessionId ?? "cli-session";
            await proxy.ClearContextAsync(sessionId);
            Console.Out.WriteLine($"cleared context for session '{sessionId}'");
            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
