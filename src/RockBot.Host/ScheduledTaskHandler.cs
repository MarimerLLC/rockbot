using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Handles <see cref="ScheduledTaskMessage"/> by invoking the LLM with the task description.
/// The LLM executes the task as if it received a user message.
/// </summary>
internal sealed class ScheduledTaskHandler : IMessageHandler<ScheduledTaskMessage>
{
    private readonly ILlmClient _llmClient;
    private readonly ILogger<ScheduledTaskHandler> _logger;

    public ScheduledTaskHandler(ILlmClient llmClient, ILogger<ScheduledTaskHandler> logger)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        _logger.LogInformation(
            "Executing scheduled task '{TaskName}'", message.TaskName);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are an autonomous agent. A scheduled task has fired and you must execute it " +
                "now. Use your available tools as needed to complete the task."),
            new(ChatRole.User,
                $"Scheduled task '{message.TaskName}': {message.Description}")
        };

        var response = await _llmClient.GetResponseAsync(
            messages,
            cancellationToken: context.CancellationToken);

        _logger.LogInformation(
            "Scheduled task '{TaskName}' completed. Response: {Response}",
            message.TaskName, response.Text);
    }
}
