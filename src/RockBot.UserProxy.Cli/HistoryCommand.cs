using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Prints the agent's stored conversation history for a session, one turn per
/// line: <c>[timestamp] role: content</c>.
/// </summary>
internal sealed class HistoryCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            var sessionId = settings.SessionId ?? "cli-session";

            var response = await proxy.GetHistoryAsync(sessionId);
            if (response is null)
            {
                Console.Error.WriteLine("error: agent did not respond before timeout");
                return 2;
            }

            if (response.Turns.Count == 0)
            {
                Console.Error.WriteLine($"(no history for session '{sessionId}')");
                return 0;
            }

            foreach (var turn in response.Turns)
            {
                var who = turn.AgentName is { Length: > 0 } a ? $"{turn.Role}/{a}" : turn.Role;
                Console.Out.WriteLine($"[{turn.Timestamp:O}] {who}: {turn.Content}");
            }

            return 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
