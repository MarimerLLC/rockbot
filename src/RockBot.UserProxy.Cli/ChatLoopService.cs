using System.Diagnostics;
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

            // Tracks the latest intermediate progress message from the agent.
            // Updated via IProgress<AgentReply> as IsFinal=false replies arrive.
            string? progressText = null;
            var progress = new Progress<AgentReply>(r => progressText = r.Content);

            AgentReply? reply = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Blue))
                .StartAsync("Thinking...", async ctx =>
                {
                    var sw = Stopwatch.StartNew();
                    var replyTask = proxy.SendAsync(message, progress: progress, cancellationToken: stoppingToken);

                    while (!replyTask.IsCompleted)
                    {
                        var elapsed = (int)sw.Elapsed.TotalSeconds;

                        // Once the agent sends an ack, show it in the spinner instead of the timer.
                        ctx.Status(progressText is not null
                            ? Markup.Escape(progressText)
                            : elapsed switch
                            {
                                < 5  => "Thinking...",
                                < 15 => $"Working on it... ({elapsed}s)",
                                < 30 => $"Still thinking... ({elapsed}s)",
                                _    => $"Complex request, please wait... ({elapsed}s)"
                            });

                        await Task.WhenAny(replyTask, Task.Delay(1000, stoppingToken));
                    }

                    reply = await replyTask;
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
