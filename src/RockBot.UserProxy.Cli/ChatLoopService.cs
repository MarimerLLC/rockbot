using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Background service running the interactive chat loop.
/// Prompts for user input, sends messages via the proxy, and displays replies.
/// </summary>
internal sealed class ChatLoopService(
    UserProxyService proxy,
    IUserFrontend frontend,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string SessionId = "cli-session";
    private const string UserId = "cli-user";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to let the host finish starting
        await Task.Yield();

        AnsiConsole.MarkupLine("[bold blue]RockBot User Proxy[/]");
        AnsiConsole.MarkupLine("Type a message to send to agents. Type [bold]exit[/] to quit.\n");

        while (!stoppingToken.IsCancellationRequested)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]>[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                lifetime.StopApplication();
                break;
            }

            var message = new UserMessage
            {
                Content = input,
                SessionId = SessionId,
                UserId = UserId
            };

            AgentReply? reply = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Blue))
                .StartAsync("Waiting for reply...", async ctx =>
                {
                    reply = await proxy.SendAsync(message, cancellationToken: stoppingToken);
                });

            if (reply is not null)
            {
                await frontend.DisplayReplyAsync(reply, stoppingToken);
            }
            else
            {
                await frontend.DisplayErrorAsync("No reply received (timeout)", stoppingToken);
            }

            AnsiConsole.WriteLine();
        }
    }
}
