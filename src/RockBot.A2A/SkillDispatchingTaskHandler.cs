using Microsoft.Extensions.Logging;

namespace RockBot.A2A;

/// <summary>
/// <see cref="IAgentTaskHandler"/> implementation that routes inbound task
/// requests to the registered <see cref="IAgentSkillHandler"/> whose
/// <see cref="IAgentSkillHandler.Skill"/>.Id matches <see cref="AgentTaskRequest.Skill"/>
/// (case-insensitive). Registered automatically by
/// <see cref="A2ASkillHandlerExtensions.AddSkillHandler{T}"/>.
/// </summary>
internal sealed class SkillDispatchingTaskHandler(
    IEnumerable<IAgentSkillHandler> skillHandlers,
    ILogger<SkillDispatchingTaskHandler> logger) : IAgentTaskHandler
{
    private readonly IReadOnlyDictionary<string, IAgentSkillHandler> _handlers =
        BuildIndex(skillHandlers, logger);

    public Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        if (_handlers.TryGetValue(request.Skill ?? string.Empty, out var handler))
        {
            logger.LogDebug(
                "Dispatching task {TaskId} to skill handler '{Skill}' ({HandlerType})",
                request.TaskId, handler.Skill.Id, handler.GetType().Name);
            return handler.ExecuteAsync(request, context);
        }

        logger.LogWarning(
            "No skill handler registered for skill '{Skill}' (task {TaskId}). Known skills: {Known}",
            request.Skill, request.TaskId, string.Join(", ", _handlers.Keys));

        return Task.FromResult(new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Failed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts =
                [
                    new AgentMessagePart
                    {
                        Kind = "text",
                        Text = $"Skill '{request.Skill}' is not supported by this agent."
                    }
                ]
            }
        });
    }

    private static IReadOnlyDictionary<string, IAgentSkillHandler> BuildIndex(
        IEnumerable<IAgentSkillHandler> handlers,
        ILogger logger)
    {
        var index = new Dictionary<string, IAgentSkillHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            var id = handler.Skill.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                logger.LogWarning(
                    "Skill handler {HandlerType} has empty Skill.Id — skipping",
                    handler.GetType().Name);
                continue;
            }

            if (index.TryGetValue(id, out var existing))
            {
                throw new InvalidOperationException(
                    $"Duplicate skill id '{id}' registered: " +
                    $"{existing.GetType().FullName} and {handler.GetType().FullName}. " +
                    "Each skill id must be handled by exactly one IAgentSkillHandler.");
            }

            index[id] = handler;
        }
        return index;
    }
}
