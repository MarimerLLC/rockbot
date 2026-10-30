using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RockBot.UserProxy.Cli.Auth;

/// <summary>
/// <c>rockbot auth mcp [server]</c> — signs the user in to an OAuth-protected MCP server the agent
/// uses (#738). The agent runs the OAuth client; this command only runs the browser leg: it asks
/// the agent for the authorization URL, receives the browser's redirect on a loopback listener
/// (or a URL the user pastes), and relays it back. Without a server it lists the agent's OAuth
/// servers and where each stands.
/// </summary>
internal sealed class McpAuthCommand : AsyncCommand<McpAuthCommand.Settings>
{
    public sealed class Settings : CommonSettings
    {
        [CommandArgument(0, "[SERVER]")]
        [Description("The MCP server to sign in to, as named in the agent's mcp.json. Omit to list them.")]
        public string? Server { get; init; }

        [CommandOption("--port <PORT>")]
        [Description("Loopback port for the redirect (default: a free one). Use one the server's client registration allows.")]
        public int? Port { get; init; }

        [CommandOption("--redirect-uri <URI>")]
        [Description("Send the browser back here instead of to a loopback listener (e.g. the web chat's /mcp-oauth/callback, which relays it).")]
        public string? RedirectUri { get; init; }

        [CommandOption("--paste")]
        [Description("Don't listen: paste the address the browser ends on after signing in.")]
        public bool Paste { get; init; }

        [CommandOption("--no-browser")]
        [Description("Print the sign-in link without trying to open a browser.")]
        public bool NoBrowser { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var host = HostFactory.Build(settings, useRichFrontend: false);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetRequiredService<UserProxyService>();
            using var cts = new CancellationTokenSource();
            using var ctrlC = ConfigureCancellation(cts);

            return string.IsNullOrWhiteSpace(settings.Server)
                ? await ListAsync(proxy, cts.Token)
                : await SignInAsync(proxy, settings, settings.Server.Trim(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130; // SIGINT convention
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

    private static async Task<int> ListAsync(UserProxyService proxy, CancellationToken ct)
    {
        var list = await proxy.ListMcpSignInsAsync(cancellationToken: ct);
        if (list is null)
        {
            AnsiConsole.MarkupLine("[red]The agent did not respond before the timeout.[/]");
            return 2;
        }
        if (list.Servers.Count == 0)
        {
            AnsiConsole.MarkupLine("The agent has no MCP servers that use OAuth sign-in.");
            return 0;
        }

        var table = new Table().AddColumns("Server", "Sign-in", "Connected");
        foreach (var server in list.Servers)
        {
            var state = server.State switch
            {
                McpSignInState.SignedIn => "[green]signed in[/]",
                McpSignInState.Pending => "[yellow]in progress[/]",
                _ => "[red]needs sign-in[/]",
            };
            table.AddRow(server.ServerName.EscapeMarkup(), state, server.Connected ? "yes" : "no");
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> SignInAsync(UserProxyService proxy, Settings settings, string server, CancellationToken ct)
    {
        // The loopback listener unless the browser goes back somewhere else.
        using var listener = settings.Paste || settings.RedirectUri is not null
            ? null
            : LoopbackRedirectListener.Start(settings.Port ?? 0);
        var redirectUri = listener?.RedirectUri
                          ?? settings.RedirectUri
                          ?? $"http://127.0.0.1:{settings.Port ?? 8080}/callback";

        var start = await proxy.StartMcpSignInAsync(server, redirectUri, TimeSpan.FromSeconds(90), ct);
        if (start is null)
        {
            AnsiConsole.MarkupLine("[red]The agent did not respond before the timeout.[/]");
            return 2;
        }
        if (start.Error is not null)
        {
            AnsiConsole.MarkupLine($"[red]Sign-in could not start:[/] {start.Error.EscapeMarkup()}");
            return 1;
        }
        if (start.AuthorizationUri is null)
        {
            AnsiConsole.MarkupLine($"[green]{server.EscapeMarkup()} didn't ask for a sign-in; the agent is connected to it.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Sign in to {server.EscapeMarkup()}:[/]");
        AnsiConsole.WriteLine(start.AuthorizationUri);
        if (!settings.NoBrowser)
            TryOpenBrowser(start.AuthorizationUri);
        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to cancel.[/]");

        if (listener is not null)
        {
            var callback = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for the browser to come back...", _ => listener.WaitAsync(ct));
            await proxy.SendMcpSignInCallbackAsync(callback, ct);
        }
        else if (settings.Paste)
        {
            McpSignInCallback? callback = null;
            while (callback is null)
            {
                var pasted = AnsiConsole.Ask<string>("Paste the address your browser ended on:");
                callback = LoopbackRedirectListener.ParseRedirect(pasted);
                if (callback is null)
                    AnsiConsole.MarkupLine("[yellow]That address has no sign-in state in it; paste the whole URL from the address bar.[/]");
            }
            await proxy.SendMcpSignInCallbackAsync(callback, ct);
        }
        // Otherwise the page at --redirect-uri relays the redirect; just wait for the outcome.

        var wait = (start.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var outcome = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Finishing the sign-in...",
                _ => proxy.WaitForMcpSignInAsync(start.SignInId!, wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(30), ct));

        switch (outcome?.State)
        {
            case McpSignInState.Completed:
                AnsiConsole.MarkupLine($"[green]Signed in. The agent is connected to {server.EscapeMarkup()}.[/]");
                return 0;
            case null:
                AnsiConsole.MarkupLine("[red]The agent didn't report how the sign-in ended.[/] Check `rockbot auth mcp` for its state.");
                return 2;
            default:
                AnsiConsole.MarkupLine($"[red]Sign-in {outcome.State}:[/] {(outcome.Message ?? string.Empty).EscapeMarkup()}");
                return 1;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser here (a server, a container): the printed link is enough.
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
