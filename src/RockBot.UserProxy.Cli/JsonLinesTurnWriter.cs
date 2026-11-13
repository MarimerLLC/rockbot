using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Writes one turn's events as AG-UI JSON, one object per line (<c>rockbot chat --output jsonl</c>,
/// #797). The first line is always <c>RUN_STARTED</c> and the last <c>RUN_FINISHED</c> or
/// <c>RUN_ERROR</c>; anything that arrives after that is dropped.
/// </summary>
/// <remarks>
/// Thread-safe. Events come from the proxy's message-handling thread (progress, run events),
/// from fire-and-forget frontend calls (subagents) and from the command itself; each line is
/// mapped, written and flushed under one lock, so lines never interleave and the mapper's state
/// stays consistent.
/// </remarks>
internal sealed class JsonLinesTurnWriter : IOneShotOutput, ITurnObserver, IProgress<AgentReply>
{
    // AG-UI's own serializer context, so the JSON is the SDK's. The relaxed encoder keeps
    // arrows, quotes and markup readable; every line is still valid JSON.
    private static readonly AGUIJsonSerializerContext Json = new(
        new JsonSerializerOptions(AGUIJsonSerializerContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private readonly Lock _lock = new();
    private readonly TextWriter _out;
    private readonly TurnEventMapper _mapper;
    private bool _started;
    private bool _closed;

    public JsonLinesTurnWriter(TextWriter output, string threadId, string runId, TimeProvider? clock = null)
    {
        _out = output;
        _mapper = new TurnEventMapper(threadId, runId, clock);
    }

    /// <summary>A UTF-8 writer on stdout, whatever the console's code page.</summary>
    public static TextWriter StandardOutput() =>
        new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

    /// <summary>The session the turn belongs to, for filtering broadcast events.</summary>
    public string ThreadId => _mapper.ThreadId;

    public string RunId => _mapper.RunId;

    /// <summary>True once the terminal event has been written.</summary>
    public bool IsClosed
    {
        get { lock (_lock) return _closed; }
    }

    /// <summary>Writes <c>RUN_STARTED</c>. Only the first call does anything.</summary>
    public void Start() => Emit(m => _started ? [] : [m.RunStarted()]);

    public void AttachmentSent(AgentAttachment attachment) => Emit(m => [m.AttachmentSent(attachment)]);

    public void Report(AgentReply reply) => Emit(m => m.Progress(reply));

    public void OnRunStarted(RunStarted run) => Emit(m => [m.AgentRunStarted(run)]);

    public void OnRunHeartbeat(RunHeartbeat heartbeat) => Emit(m => [m.Heartbeat(heartbeat)]);

    // The agent's RunFinished is folded into the TurnResult, which Finish writes.
    public void OnRunFinished(RunFinished run) { }

    public void OnTaskListSnapshot(TaskListSnapshot snapshot) => Emit(m => [m.TaskListSnapshot(snapshot)]);

    public void OnTaskListDelta(TaskListDelta delta) => Emit(m => [m.TaskListDelta(delta)]);

    public void SubagentStarted(SubagentStarted started) => Emit(m => [m.SubagentStarted(started)]);

    public void SubagentFinished(SubagentFinished finished) => Emit(m => [m.SubagentFinished(finished)]);

    public void UnsolicitedReply(AgentReply reply) => Emit(m => m.UnsolicitedReply(reply));

    /// <summary>Writes the final reply and the terminal event, and closes the stream.</summary>
    public void Finish(TurnResult result) => Emit(m => m.Finish(result));

    /// <summary>Ends the stream with <c>RUN_ERROR</c> (writing <c>RUN_STARTED</c> first if it never was).</summary>
    public void Fail(string code, string message) => Emit(m => [m.RunError(code, message)]);

    // IOneShotOutput: the run id is the turn's correlation id, so the agent's run events line up.
    string? IOneShotOutput.CorrelationId => RunId;
    IProgress<AgentReply> IOneShotOutput.Progress => this;
    ITurnObserver IOneShotOutput.Observer => this;
    void IOneShotOutput.AttachmentStaged(AgentAttachment attachment) => AttachmentSent(attachment);
    void IOneShotOutput.AttachmentFailed(string error) => Fail(TurnEventMapper.ErrorCodes.AttachmentFailed, error);

    private void Emit(Func<TurnEventMapper, IEnumerable<BaseEvent>> map)
    {
        lock (_lock)
        {
            if (_closed)
                return;

            var events = map(_mapper).ToList();
            if (events.Count == 0)
                return;

            // The contract is that RUN_STARTED comes first, even for a failure before the turn.
            if (!_started && events[0] is not RunStartedEvent)
                events.Insert(0, _mapper.RunStarted());

            foreach (var e in events)
            {
                _started |= e is RunStartedEvent;
                _out.WriteLine(JsonSerializer.Serialize(e, Json.BaseEvent));
                if (TurnEventMapper.IsTerminal(e))
                {
                    _closed = true;
                    break;
                }
            }
            _out.Flush();
        }
    }
}
