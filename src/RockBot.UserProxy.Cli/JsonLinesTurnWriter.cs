using System.Text;
using RockBot.UserProxy.AgUi;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Writes one turn's events as AG-UI JSON, one object per line (<c>rockbot chat --output jsonl</c>,
/// #797). The first line is always <c>RUN_STARTED</c> and the last <c>RUN_FINISHED</c> or
/// <c>RUN_ERROR</c>; anything that arrives after that is dropped.
/// </summary>
/// <remarks>
/// A <see cref="TurnEventEmitter"/> whose sink writes and flushes a line. The emitter calls the
/// sink under its lock, so lines never interleave.
/// </remarks>
internal sealed class JsonLinesTurnWriter(TextWriter output, string threadId, string runId, TimeProvider? clock = null)
    : TurnEventEmitter(threadId, runId, e => WriteLine(output, AgUiJson.Serialize(e)), clock), IOneShotOutput
{
    /// <summary>A UTF-8 writer on stdout, whatever the console's code page.</summary>
    public static TextWriter StandardOutput() =>
        new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

    private static void WriteLine(TextWriter output, string line)
    {
        output.WriteLine(line);
        output.Flush();
    }

    // IOneShotOutput: the run id is the turn's correlation id, so the agent's run events line up.
    string? IOneShotOutput.CorrelationId => RunId;
    IProgress<AgentReply> IOneShotOutput.Progress => this;
    ITurnObserver IOneShotOutput.Observer => this;
    void IOneShotOutput.AttachmentStaged(AgentAttachment attachment) => AttachmentSent(attachment);
    void IOneShotOutput.AttachmentFailed(string error) => Fail(TurnEventMapper.ErrorCodes.AttachmentFailed, error);
    Task IOneShotOutput.FinishAsync(TurnResult result) => FinishAsync(result, DefaultSettleQuiet, DefaultSettleMax);
}
