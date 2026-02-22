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

    public ReportProgressFunctions(
        string taskId,
        string primarySessionId,
        IMessagePublisher publisher,
        string subagentId,
        ILogger logger)
    {
        _taskId = taskId;
        _primarySessionId = primarySessionId;
        _publisher = publisher;
        _subagentId = subagentId;
        _logger = logger;

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
        await _publisher.PublishAsync(SubagentTopics.Progress, envelope, CancellationToken.None);

        _logger.LogInformation("Subagent {TaskId} reported progress: {Message}", _taskId, message);
        return "Progress reported.";
    }
}
