using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        // With --output jsonl (#797), RUN_STARTED goes out before connecting, so even a turn that
        // never reaches the agent has a first and a last line.
        using var jsonOut = settings.JsonLines ? JsonLinesTurnWriter.StandardOutput() : null;
        var writer = jsonOut is null
            ? null
            : new JsonLinesTurnWriter(jsonOut, threadId: settings.EffectiveSessionId, runId: Guid.NewGuid().ToString("N"));
        writer?.Start();

        int? exitCode = null;
        try
        {
            using var host = HostFactory.Build(
                settings,
                useRichFrontend: !oneShot,
                frontend: writer is null ? null : new JsonLinesFrontend(writer));

            await host.StartAsync();
            try
            {
                var proxy = host.Services.GetRequiredService<UserProxyService>();

                exitCode = oneShot
                    ? await RunOneShotAsync(proxy, settings, (IOneShotOutput?)writer ?? new TextOneShotOutput())
                    : await RunInteractiveAsync(proxy, settings, host);
                return exitCode.Value;
            }
            finally
            {
                await host.StopAsync();
            }
        }
        catch (Exception ex) when (writer is not null)
        {
            // Stdout is the JSON stream, so no exception may reach Spectre, which prints it there.
            Console.Error.WriteLine($"error: {ex.Message}");

            // Shutting down failed after the turn ended: the stream is complete, and the exit code
            // is the turn's.
            if (writer.IsClosed)
                return exitCode ?? 1;

            // Connecting to the broker, or the bus itself, failed.
            writer.Fail(TurnEventMapper.ErrorCodes.ClientError, ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Sends each file to the agent, which writes it to the shared volume and returns a path
    /// reference. The CLI has no access to that volume — it may not even be on the same machine —
    /// so the bytes go over the bus and only the reference rides on the message.
    /// </summary>
    /// <param name="onStaged">Called for each file the agent accepted.</param>
    /// <returns>
    /// The staged references, a process exit code (non-zero if any file failed), and what went
    /// wrong, which is also written to stderr.
    /// </returns>
    internal static async Task<(IReadOnlyList<AgentAttachment> Attachments, int ExitCode, string? Error)>
        UploadAttachmentsAsync(UserProxyService proxy, string[] paths, string sessionId, string userId,
            Action<AgentAttachment> onStaged)
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
            onStaged(response.Attachment);
        }

        return (staged, 0, null);
    }

    /// <summary>
    /// One turn: read the message, upload its attachments, send it and follow it to its end.
    /// <paramref name="output"/> decides where it all goes (text or JSON lines).
    /// </summary>
    private static async Task<int> RunOneShotAsync(UserProxyService proxy, Settings settings, IOneShotOutput output)
    {
        var content = settings.Message == "-"
            ? await Console.In.ReadToEndAsync()
            : settings.Message!;

        IReadOnlyList<AgentAttachment>? attachments = null;
        if (settings.Attach is { Length: > 0 })
        {
            var (uploaded, exitCode, error) = await UploadAttachmentsAsync(
                proxy, settings.Attach, settings.EffectiveSessionId, settings.EffectiveUserId, output.AttachmentStaged);
            if (exitCode != 0)
            {
                output.AttachmentFailed(error ?? "an attachment could not be sent");
                return exitCode;
            }
            attachments = uploaded;
        }

        var message = new UserMessage
        {
            Content = content,
            SessionId = settings.EffectiveSessionId,
            UserId = settings.EffectiveUserId,
            TargetAgent = settings.TargetAgent,
            ClientCapabilities = ClientCapabilityPresets.Cli,
            ChannelName = "cli",
            Attachments = attachments
        };

        var result = await proxy.SendTurnAsync(message, new TurnOptions
        {
            // JSON lines uses the turn's correlation id as its run id, so the agent's run events line up.
            CorrelationId = output.CorrelationId,
            Progress = output.Progress,
            Observer = output.Observer,
        });

        if (ChatExitCodes.Describe(result) is { } problem)
            Console.Error.WriteLine($"error: {problem}");

        await output.FinishAsync(result);
        return ChatExitCodes.For(result.Outcome);
    }

    private static async Task<int> RunInteractiveAsync(UserProxyService proxy, Settings settings, IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stoppingToken = lifetime.ApplicationStopping;
        var sessionId = settings.EffectiveSessionId;
        var userId = settings.EffectiveUserId;

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
