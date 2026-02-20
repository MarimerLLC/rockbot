using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Handles <see cref="ScheduledTaskMessage"/> by invoking the LLM with the task description
/// and publishing the response as an <see cref="AgentReply"/> so the user sees it in the UI.
/// </summary>
internal sealed class ScheduledTaskHandler : IMessageHandler<ScheduledTaskMessage>
{
    private readonly ILlmClient _llmClient;
    private readonly IMessagePublisher _publisher;
    private readonly AgentIdentity _agent;
    private readonly ILogger<ScheduledTaskHandler> _logger;

    public ScheduledTaskHandler(
        ILlmClient llmClient,
        IMessagePublisher publisher,
        AgentIdentity agent,
        ILogger<ScheduledTaskHandler> logger)
    {
        _llmClient = llmClient;
        _publisher = publisher;
        _agent = agent;
        _logger = logger;
    }

    public async Task HandleAsync(ScheduledTaskMessage message, MessageHandlerContext context)
    {
        _logger.LogInformation("Executing scheduled task '{TaskName}'", message.TaskName);

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

        var text = response.Text ?? string.Empty;

        _logger.LogInformation(
            "Scheduled task '{TaskName}' completed. Response: {Response}",
            message.TaskName, text);

        var reply = new AgentReply
        {
            Content = text,
            SessionId = "scheduled",
            AgentName = _agent.Name,
            IsFinal = true
        };

        var envelope = reply.ToEnvelope<AgentReply>(source: _agent.Name);
        await _publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, context.CancellationToken);
    }
}
