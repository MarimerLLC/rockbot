using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Where a one-shot <c>rockbot chat --message</c> turn goes: <see cref="TextOneShotOutput"/> for
/// <c>--output text</c>, <see cref="JsonLinesTurnWriter"/> for <c>--output jsonl</c>. The turn
/// itself (reading the message, uploading attachments, sending, mapping the exit code) is the
/// same for both.
/// </summary>
internal interface IOneShotOutput
{
    /// <summary>The correlation id to send the turn with, or null for a fresh one.</summary>
    string? CorrelationId { get; }

    IProgress<AgentReply> Progress { get; }

    ITurnObserver Observer { get; }

    /// <summary>The agent accepted an <c>--attach</c> file.</summary>
    void AttachmentStaged(AgentAttachment attachment);

    /// <summary>An <c>--attach</c> file failed, so no message is sent. The error is already on stderr.</summary>
    void AttachmentFailed(string error);

    /// <summary>The turn ended. Any problem has already been written to stderr.</summary>
    void Finish(TurnResult result);
}

/// <summary>
/// <c>--output text</c>: the final reply (and a placeholder line per attachment) on stdout, and
/// progress and the task list on stderr, so a caller piping stdout gets only the reply.
/// </summary>
internal sealed class TextOneShotOutput : IOneShotOutput
{
    // Written synchronously, in arrival order.
    private readonly CliTurnReporter _reporter = new(Console.Error.WriteLine);

    public string? CorrelationId => null;
    public IProgress<AgentReply> Progress => _reporter;
    public ITurnObserver Observer => _reporter;

    public void AttachmentStaged(AgentAttachment attachment) =>
        Console.Error.WriteLine($"sent {AttachmentPlaceholder.Render(attachment)}");

    public void AttachmentFailed(string error) { }

    public void Finish(TurnResult result)
    {
        if (result.Reply is not { } reply)
            return;

        Console.Out.WriteLine(HtmlPlainTextRenderer.StripHtml(reply.Content));

        // The one-shot form can't render binaries inline, so print a placeholder line per
        // attachment — to stdout, alongside the reply content, matching PlainConsoleFrontend —
        // so the caller knows an attachment was produced (and where).
        if (reply.Attachments is { Count: > 0 })
        {
            foreach (var att in reply.Attachments)
                Console.Out.WriteLine(AttachmentPlaceholder.Render(att));
        }
    }
}
