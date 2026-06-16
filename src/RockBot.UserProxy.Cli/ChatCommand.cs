using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.UserProxy.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Default subcommand. With no arguments, runs an interactive REPL identical
/// to the original CLI. With <c>--message</c>, sends a single prompt, prints
/// the final reply to stdout, and exits — the form Claude/scripts use to
/// drive the agent without a TTY.
/// </summary>
internal sealed class ChatCommand : AsyncCommand<ChatCommand.Settings>
{
    public sealed class Settings : CommonSettings
    {
        [CommandOption("-m|--message <TEXT>")]
        [Description("Send a single message and exit (one-shot mode). Use '-' to read from stdin.")]
        public string? Message { get; init; }

        [CommandOption("--target <AGENT>")]
        [Description("Optional TargetAgent name on the message envelope (defaults to broadcast)")]
        public string? TargetAgent { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var oneShot = settings.Message is not null;
        using var host = HostFactory.Build(settings, useRichFrontend: !oneShot);

        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();

            return oneShot
                ? await RunOneShotAsync(proxy, settings)
                : await RunInteractiveAsync(proxy, settings, host);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<int> RunOneShotAsync(UserProxyService proxy, Settings settings)
    {
        var content = settings.Message == "-"
            ? await Console.In.ReadToEndAsync()
            : settings.Message!;

        var message = new UserMessage
        {
            Content = content,
            SessionId = settings.SessionId ?? "cli-session",
            UserId = settings.UserId ?? "cli-user",
            TargetAgent = settings.TargetAgent,
            ClientCapabilities = ClientCapabilityPresets.Cli,
            ChannelName = "cli"
        };

        // Mirror intermediate progress to stderr so callers piping stdout get
        // only the final reply text.
        var progress = new Progress<AgentReply>(r =>
            Console.Error.WriteLine($"[{r.AgentName}] {HtmlPlainTextRenderer.StripHtml(r.Content)}"));

        var reply = await proxy.SendAsync(message, progress: progress);

        if (reply is null)
        {
            Console.Error.WriteLine("error: no reply received (timeout)");
            return 2;
        }

        Console.Out.WriteLine(HtmlPlainTextRenderer.StripHtml(reply.Content));

        // The one-shot form can't render binaries inline, so print a placeholder line per
        // attachment — to stdout, alongside the reply content, matching PlainConsoleFrontend —
        // so the caller knows an attachment was produced (and where).
        if (reply.Attachments is { Count: > 0 })
        {
            foreach (var att in reply.Attachments)
                Console.Out.WriteLine(AttachmentPlaceholder.Render(att));
        }
        return 0;
    }

    private static async Task<int> RunInteractiveAsync(UserProxyService proxy, Settings settings, IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stoppingToken = lifetime.ApplicationStopping;
        var sessionId = settings.SessionId ?? "cli-session";
        var userId = settings.UserId ?? "cli-user";

        AnsiConsole.MarkupLine($"[bold blue]RockBot User Proxy[/] [dim]v{Markup.Escape(AssemblyVersion.Current)}[/]");

        try
        {
            var info = await proxy.GetAgentInfoAsync(timeout: TimeSpan.FromSeconds(5), cancellationToken: stoppingToken);
            if (info is not null)
                AnsiConsole.MarkupLine($"[dim]Connected to {Markup.Escape(info.AgentName)} v{Markup.Escape(info.AgentVersion)}[/]");
            else
                AnsiConsole.MarkupLine("[dim yellow]Agent not available[/]");
        }
        catch
        {
            AnsiConsole.MarkupLine("[dim yellow]Agent not available[/]");
        }

        AnsiConsole.MarkupLine("Type a message to send to agents. Type [bold]exit[/] to quit.\n");

        var frontend = host.Services.GetRequiredService<IUserFrontend>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]>[/]")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            var message = new UserMessage
            {
                Content = input,
                SessionId = sessionId,
                UserId = userId,
                TargetAgent = settings.TargetAgent,
                ClientCapabilities = ClientCapabilityPresets.Cli,
                ChannelName = "cli"
            };

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
                await frontend.DisplayReplyAsync(reply, stoppingToken);
            else
                await frontend.DisplayErrorAsync("No reply received (timeout)", stoppingToken);

            AnsiConsole.WriteLine();
        }

        return 0;
    }
}
