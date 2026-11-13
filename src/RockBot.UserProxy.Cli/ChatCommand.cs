using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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
        [Description("Send a single message and exit (one-shot mode). Use '-' to read from stdin. " + ChatExitCodes.HelpText)]
        public string? Message { get; init; }

        [CommandOption("--target <AGENT>")]
        [Description("Optional TargetAgent name on the message envelope (defaults to broadcast)")]
        public string? TargetAgent { get; init; }

        [CommandOption("--attach <PATH>")]
        [Description("Attach a file to the message (repeatable). Requires --message.")]
        public string[]? Attach { get; init; }

        [CommandOption("--output <FORMAT>")]
        [Description("Output format for --message: 'text' (default: the reply on stdout, progress on stderr) or " +
            "'jsonl' (every event of the turn on stdout as AG-UI JSON, one object per line, from RUN_STARTED " +
            "to RUN_FINISHED or RUN_ERROR). Exit codes are the same for both.")]
        [DefaultValue(OutputFormats.Text)]
        public string Output { get; init; } = OutputFormats.Text;

        public bool JsonLines => string.Equals(Output, OutputFormats.JsonLines, StringComparison.OrdinalIgnoreCase);

        public override ValidationResult Validate() =>
            JsonLines || string.Equals(Output, OutputFormats.Text, StringComparison.OrdinalIgnoreCase)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--output must be '{OutputFormats.Text}' or '{OutputFormats.JsonLines}', not '{Output}'.");
    }

    /// <summary>Values of <c>--output</c>.</summary>
    public static class OutputFormats
    {
        public const string Text = "text";
        public const string JsonLines = "jsonl";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var oneShot = settings.Message is not null;

        // Attaching per-message in an interactive session needs an affordance the REPL does not
        // have; rather than quietly attaching to whichever message happens to go first, say so.
        if (settings.Attach is { Length: > 0 } && !oneShot)
        {
            Console.Error.WriteLine("error: --attach requires --message (one-shot mode).");
            return 1;
        }

        // The JSON-lines stream describes one turn; the REPL has many, and its own screen.
        if (settings.JsonLines && !oneShot)
        {
            Console.Error.WriteLine("error: --output jsonl requires --message (one-shot mode).");
            return 1;
        }

        if (settings.JsonLines)
            return await RunJsonLinesAsync(settings);

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

    /// <summary>
    /// Sends each file to the agent, which writes it to the shared volume and returns a path
    /// reference. The CLI has no access to that volume — it may not even be on the same machine —
    /// so the bytes go over the bus and only the reference rides on the message.
    /// </summary>
    /// <param name="onStaged">
    /// Called for each file the agent accepted. Without it, a <c>sent …</c> line goes to stderr.
    /// </param>
    /// <returns>
    /// The staged references, a process exit code (non-zero if any file failed), and what went
    /// wrong, which is also written to stderr.
    /// </returns>
    internal static async Task<(IReadOnlyList<AgentAttachment> Attachments, int ExitCode, string? Error)>
        UploadAttachmentsAsync(UserProxyService proxy, string[] paths, string sessionId, string userId,
            Action<AgentAttachment>? onStaged = null)
    {
        var staged = new List<AgentAttachment>(paths.Length);

        (IReadOnlyList<AgentAttachment>, int, string?) Fail(int exitCode, string error)
        {
            Console.Error.WriteLine($"error: {error}");
            return (staged, exitCode, error);
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path))
                return Fail(1, $"no such file: {path}");

            var data = await File.ReadAllBytesAsync(path);
            var fileName = Path.GetFileName(path);

            var response = await proxy.UploadAttachmentAsync(new AttachmentUploadRequest
            {
                FileName = fileName,
                Mime = AttachmentMimeGuess.FromFileName(fileName),
                Data = data,
                SessionId = sessionId,
                UserId = userId
            });

            if (response is null)
                return Fail(2, $"{fileName} could not be sent — the agent did not respond.");

            if (!response.Success || response.Attachment is null)
                return Fail(1, $"{fileName} was not accepted — {response.Error}");

            staged.Add(response.Attachment);
            if (onStaged is not null)
                onStaged(response.Attachment);
            else
                Console.Error.WriteLine($"sent {AttachmentPlaceholder.Render(response.Attachment)}");
        }

        return (staged, 0, null);
    }

    private static async Task<string> ReadContentAsync(Settings settings) =>
        settings.Message == "-"
            ? await Console.In.ReadToEndAsync()
            : settings.Message!;

    private static UserMessage OneShotMessage(
        Settings settings, string content, IReadOnlyList<AgentAttachment>? attachments) => new()
    {
        Content = content,
        SessionId = settings.SessionId ?? "cli-session",
        UserId = settings.UserId ?? "cli-user",
        TargetAgent = settings.TargetAgent,
        ClientCapabilities = ClientCapabilityPresets.Cli,
        ChannelName = "cli",
        Attachments = attachments
    };

    /// <summary>
    /// One-shot with <c>--output jsonl</c> (#797): the turn's events on stdout as AG-UI JSON
    /// lines. <c>RUN_STARTED</c> goes out before connecting, so even a turn that never reaches
    /// the agent has a first and a last line; stderr keeps only <c>error:</c> diagnostics.
    /// </summary>
    private static async Task<int> RunJsonLinesAsync(Settings settings)
    {
        using var stdout = JsonLinesTurnWriter.StandardOutput();
        var writer = new JsonLinesTurnWriter(
            stdout, threadId: settings.SessionId ?? "cli-session", runId: Guid.NewGuid().ToString("N"));
        writer.Start();

        try
        {
            using var host = HostFactory.Build(
                settings,
                useRichFrontend: false,
                // The console logger writes to stdout by default; stdout is the JSON stream here.
                configure: b => b.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace),
                frontend: new JsonLinesFrontend(writer));
            await host.StartAsync();
            try
            {
                var proxy = host.Services.GetRequiredService<UserProxyService>();
                return await RunJsonLinesTurnAsync(proxy, settings, writer);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        catch (Exception ex) when (!writer.IsClosed)
        {
            // Connecting to the broker, or the bus itself, failed: end the stream rather than let
            // Spectre print the exception to stdout.
            Console.Error.WriteLine($"error: {ex.Message}");
            writer.Fail(TurnEventMapper.ErrorCodes.ClientError, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunJsonLinesTurnAsync(UserProxyService proxy, Settings settings, JsonLinesTurnWriter writer)
    {
        var content = await ReadContentAsync(settings);

        IReadOnlyList<AgentAttachment>? attachments = null;
        if (settings.Attach is { Length: > 0 })
        {
            var (uploaded, exitCode, error) = await UploadAttachmentsAsync(
                proxy, settings.Attach, writer.ThreadId, settings.UserId ?? "cli-user", writer.AttachmentSent);
            if (exitCode != 0)
            {
                writer.Fail(TurnEventMapper.ErrorCodes.AttachmentFailed, error ?? "an attachment could not be sent");
                return exitCode;
            }
            attachments = uploaded;
        }

        // The run id is the turn's correlation id, so the agent's run events line up with ours.
        var result = await proxy.SendTurnAsync(OneShotMessage(settings, content, attachments), new TurnOptions
        {
            CorrelationId = writer.RunId,
            Progress = writer,
            Observer = writer,
        });

        if (ChatExitCodes.Describe(result) is { } problem)
            Console.Error.WriteLine($"error: {problem}");

        writer.Finish(result);
        return ChatExitCodes.For(result.Outcome);
    }

    private static async Task<int> RunOneShotAsync(UserProxyService proxy, Settings settings)
    {
        var content = await ReadContentAsync(settings);

        var sessionId = settings.SessionId ?? "cli-session";
        var userId = settings.UserId ?? "cli-user";

        IReadOnlyList<AgentAttachment>? attachments = null;
        if (settings.Attach is { Length: > 0 })
        {
            var (uploaded, exitCode, _) = await UploadAttachmentsAsync(
                proxy, settings.Attach, sessionId, userId);
            if (exitCode != 0)
                return exitCode;
            attachments = uploaded;
        }

        var message = OneShotMessage(settings, content, attachments);

        // Mirror intermediate progress (and the task list) to stderr so callers piping stdout
        // get only the final reply text. Written synchronously, in arrival order.
        var reporter = new CliTurnReporter(Console.Error.WriteLine);

        var result = await proxy.SendTurnAsync(message, new TurnOptions
        {
            Progress = reporter,
            Observer = reporter,
        });

        if (ChatExitCodes.Describe(result) is { } problem)
            Console.Error.WriteLine($"error: {problem}");

        var reply = result.Reply;
        if (reply is null)
            return ChatExitCodes.For(result.Outcome);

        Console.Out.WriteLine(HtmlPlainTextRenderer.StripHtml(reply.Content));

        // The one-shot form can't render binaries inline, so print a placeholder line per
        // attachment — to stdout, alongside the reply content, matching PlainConsoleFrontend —
        // so the caller knows an attachment was produced (and where).
        if (reply.Attachments is { Count: > 0 })
        {
            foreach (var att in reply.Attachments)
                Console.Out.WriteLine(AttachmentPlaceholder.Render(att));
        }
        return ChatExitCodes.For(result.Outcome);
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

            // No line writer: the spinner shows the latest status and the task list instead.
            var reporter = new CliTurnReporter();

            TurnResult? result = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Blue))
                .StartAsync("Thinking...", async ctx =>
                {
                    var sw = Stopwatch.StartNew();
                    var turnTask = proxy.SendTurnAsync(message, new TurnOptions
                    {
                        Progress = reporter,
                        Observer = reporter,
                    }, stoppingToken);

                    while (!turnTask.IsCompleted)
                    {
                        var elapsed = (int)sw.Elapsed.TotalSeconds;
                        var status = reporter.Status is { } s
                            ? Markup.Escape(s)
                            : elapsed switch
                            {
                                < 5  => "Thinking...",
                                < 15 => $"Working on it... ({elapsed}s)",
                                < 30 => $"Still thinking... ({elapsed}s)",
                                _    => $"Complex request, please wait... ({elapsed}s)"
                            };
                        if (reporter.TaskLine is { } tasks)
                            status = $"[dim]{Markup.Escape(tasks)}[/] {status}";
                        ctx.Status(status);

                        await Task.WhenAny(turnTask, Task.Delay(1000, stoppingToken));
                    }

                    result = await turnTask;
                });

            if (result!.Reply is not null)
                await frontend.DisplayReplyAsync(result.Reply, stoppingToken);

            switch (result.Outcome)
            {
                case TurnResultOutcome.Success:
                    break;
                case TurnResultOutcome.Cancelled:
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(ChatExitCodes.Describe(result)!)}");
                    break;
                default:
                    await frontend.DisplayErrorAsync(ChatExitCodes.Describe(result)!, stoppingToken);
                    break;
            }

            AnsiConsole.WriteLine();
        }

        return 0;
    }
}
