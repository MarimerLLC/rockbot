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
        var panel = new Panel(Markup.Escape(reply.Content))
        {
            Header = new PanelHeader(Markup.Escape(reply.AgentName)),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Blue)
        };

        AnsiConsole.Write(panel);
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        return Task.CompletedTask;
    }
}
