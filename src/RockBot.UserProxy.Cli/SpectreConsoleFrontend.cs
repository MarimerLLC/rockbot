using Spectre.Console;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Spectre.Console implementation of <see cref="IUserFrontend"/>.
/// Renders agent replies as panels and errors as red markup.
/// </summary>
internal sealed class SpectreConsoleFrontend : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(
            reply.Origin, currentChannel: "cli", currentSessionId: reply.SessionId, DateTimeOffset.UtcNow);
        if (anchor is not null)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(anchor)}[/]");

        var panel = new Panel(SpectreMarkupConverter.ToSpectreMarkup(reply.Content))
        {
            Header = new PanelHeader(Markup.Escape(reply.AgentName)),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Blue)
        };

        AnsiConsole.Write(panel);
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        // Render ephemeral progress as a single dim line rather than a panel,
        // so unsolicited subagent / A2A progress doesn't stack as bubbles.
        AnsiConsole.MarkupLine(
            $"[dim italic]{Markup.Escape(reply.AgentName)}: {SpectreMarkupConverter.ToSpectreMarkup(reply.Content)}[/]");
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        return Task.CompletedTask;
    }
}
