using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Frontend used in non-interactive subcommands (one-shot chat, history, info,
/// status, etc.). Final and unsolicited replies go to stdout so output can be
/// piped or captured; transient progress and errors go to stderr so they don't
/// pollute the main result stream.
/// </summary>
internal sealed class PlainConsoleFrontend : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(
            reply.Origin, currentChannel: "cli", currentSessionId: reply.SessionId, DateTimeOffset.UtcNow);
        if (anchor is not null)
            Console.Out.WriteLine(anchor);
        Console.Out.WriteLine(HtmlPlainTextRenderer.StripHtml(reply.Content));

        // The plain console can't render binary attachments — print a placeholder line per
        // attachment so the user knows one was produced (and where) without dumping bytes.
        if (reply.Attachments is { Count: > 0 })
        {
            foreach (var att in reply.Attachments)
                Console.Out.WriteLine(AttachmentPlaceholder.Render(att));
        }
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"[{reply.AgentName}] {HtmlPlainTextRenderer.StripHtml(reply.Content)}");
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"error: {message}");
        return Task.CompletedTask;
    }
}
