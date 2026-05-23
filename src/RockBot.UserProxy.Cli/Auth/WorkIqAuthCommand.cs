using Microsoft.Extensions.DependencyInjection;
using RockBot.UserProxy.WorkIqAuth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli.Auth;

/// <summary>
/// <c>rockbot auth workiq</c> — drives the MSAL device-code flow to sign the
/// user into Microsoft 365 (Work IQ) and publishes the resulting token cache
/// to the agent.
/// </summary>
internal sealed class WorkIqAuthCommand : AsyncCommand<CommonSettings.Plain>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommonSettings.Plain settings)
    {
        using var host = HostFactory.Build(
            settings,
            useRichFrontend: true,
            configure: builder => builder.Services.AddWorkIqAuthClient(builder.Configuration));

        await host.StartAsync();
        try
        {
            var flow = host.Services.GetRequiredService<WorkIqDeviceCodeFlow>();
            using var cts = new CancellationTokenSource();
            using var ctrlC = ConfigureCancellation(cts);

            var (challenge, completion) = await flow.BeginAsync(cts.Token);

            AnsiConsole.MarkupLine(
                $"[bold]Open[/] [link]{challenge.VerificationUrl}[/] [bold]and enter code[/]");
            var codePanel = new Panel(new Markup($"[bold yellow]{challenge.UserCode}[/]"))
                .Padding(2, 1)
                .BorderStyle(Style.Parse("yellow"));
            AnsiConsole.Write(codePanel);
            AnsiConsole.MarkupLine($"[grey]Code expires at {challenge.ExpiresOn.LocalDateTime:t}.[/]");
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to cancel.[/]");

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for sign-in to complete...", async _ => await completion);

            AnsiConsole.MarkupLine("[green]Signed in. Token cache published to the agent.[/]");
            return 0;
        }
        catch (WorkIqAuthFlowException ex)
        {
            AnsiConsole.MarkupLine($"[red]Sign-in failed[/] ({ex.Code}): {ex.Message.EscapeMarkup()}");
            return ex.Code switch
            {
                WorkIqAuthFlowException.Codes.UserCancelled => 130, // SIGINT convention
                WorkIqAuthFlowException.Codes.NotConfigured => 78,  // EX_CONFIG
                _ => 1
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IDisposable ConfigureCancellation(CancellationTokenSource cts)
    {
        ConsoleCancelEventHandler handler = (_, args) =>
        {
            args.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        return new CancelHandlerSubscription(handler);
    }

    private sealed class CancelHandlerSubscription(ConsoleCancelEventHandler handler) : IDisposable
    {
        public void Dispose() => Console.CancelKeyPress -= handler;
    }
}
