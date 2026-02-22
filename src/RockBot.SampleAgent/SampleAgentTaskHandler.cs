using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace RockBot.SampleAgent;

/// <summary>
/// Reference implementation of <see cref="IAgentTaskHandler"/>.
/// Receives a task, publishes a Working status, calls the LLM with the task message,
/// and returns the result.
/// </summary>
internal sealed class SampleAgentTaskHandler(
    IChatClient chatClient,
    ILogger<SampleAgentTaskHandler> logger) : IAgentTaskHandler
{
    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;

        logger.LogInformation("Handling task {TaskId} (skill={Skill})", request.TaskId, request.Skill);

        // Notify caller that we're actively working
        await context.PublishStatus(new AgentTaskStatusUpdate
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Working
        }, ct);

        // Extract the text from the incoming message
        var inputText = request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? "(empty)";

        logger.LogDebug("Task {TaskId} input: {Input}", request.TaskId, inputText);

        // Call the LLM with the task
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant that completes tasks concisely."),
            new(ChatRole.User, $"Task skill: {request.Skill}\n\n{inputText}")
        };

        string outputText;
        try
        {
            var response = await chatClient.GetResponseAsync(chatMessages, cancellationToken: ct);
            outputText = response.Text ?? "(no response)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LLM call failed for task {TaskId}", request.TaskId);
            outputText = $"I was unable to complete this task due to an error: {ex.Message}";
        }

        logger.LogInformation("Task {TaskId} completed, output length={Len}", request.TaskId, outputText.Length);

        return new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = outputText }]
            }
        };
    }
}
