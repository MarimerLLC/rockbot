using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Subagent;

/// <summary>
/// LLM-callable tool that a subagent calls to report progress to the primary session.
/// Instantiated per-task with taskId and primarySessionId baked in.
/// </summary>
internal sealed class ReportProgressFunctions
{
    public IList<AITool> Tools { get; }

    private readonly string _taskId;
    private readonly string _primarySessionId;
    private readonly IMessagePublisher _publisher;
    private readonly string _subagentId;
    private readonly ILogger _logger;
    private readonly Action<string>? _onReport;

    private readonly string _agentName;

    public ReportProgressFunctions(
        string taskId,
        string primarySessionId,
        IMessagePublisher publisher,
        string subagentId,
        string agentName,
        ILogger logger,
        Action<string>? onReport = null)
    {
        _taskId = taskId;
        _primarySessionId = primarySessionId;
        _publisher = publisher;
        _subagentId = subagentId;
        _agentName = agentName;
        _logger = logger;
        _onReport = onReport;

        Tools =
        [
            AIFunctionFactory.Create(ReportProgress)
        ];
    }

    [Description("Report progress on the current task back to the primary agent session. " +
                 "Call this periodically with a short status update so the user stays informed.")]
    public async Task<string> ReportProgress(
        [Description("A short status message describing current progress")] string message)
    {
        var progress = new SubagentProgressMessage
        {
            TaskId = _taskId,
            SubagentSessionId = $"subagent-{_taskId}",
            PrimarySessionId = _primarySessionId,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow
        };

        var envelope = progress.ToEnvelope<SubagentProgressMessage>(source: _subagentId);
        await _publisher.PublishAsync($"{SubagentTopics.Progress}.{_agentName}", envelope, CancellationToken.None);

        _logger.LogInformation("Subagent {TaskId} reported progress: {Message}", _taskId, message);

        // Notify in-process listeners (e.g. SubagentRunner's rolling buffer used for
        // failure-detail reports). Errors here must not break progress reporting.
        try { _onReport?.Invoke(message); }
        catch (Exception ex) { _logger.LogDebug(ex, "Progress callback for {TaskId} threw — ignoring", _taskId); }

        return "Progress reported.";
    }
}
