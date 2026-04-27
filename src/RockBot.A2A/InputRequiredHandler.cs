using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.A2A;

/// <summary>
/// Generates a follow-up response when a remote agent returns InputRequired.
/// Trust-gated: if the target agent has Act-level trust with the skill approved,
/// the LLM generates the response autonomously. Otherwise, the question is
/// surfaced through the user's conversation first.
/// Used by both the HTTP dispatch loop (<see cref="InvokeAgentExecutor"/>) and the
/// queue-based result handler (<see cref="A2ATaskResultHandler"/>).
/// </summary>
internal sealed class InputRequiredHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    ILlmClient llmClient,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    IAgentTrustStore trustStore,
    AgentNameHolder agentNameHolder,
    ILogger<InputRequiredHandler> logger)
{
    private string DisplayName => agentNameHolder.DisplayName ?? agent.Name;
    public async Task<InputRequiredResponse> HandleAsync(
        InputRequiredContext context,
        CancellationToken ct)
    {
        using var activity = A2ADiagnostics.Source.StartActivity("rockbot.a2a.input_required_round");
        activity?.SetTag("rockbot.a2a.task_id", context.TaskId);
        activity?.SetTag("rockbot.a2a.context_id", context.ContextId);
        activity?.SetTag("rockbot.a2a.target_agent", context.TargetAgent);
        activity?.SetTag("rockbot.a2a.round", context.Round);
        activity?.SetTag("rockbot.a2a.session_id", context.PrimarySessionId);

        // Check trust level for the target agent
        var trust = await trustStore.GetOrCreateAsync(context.TargetAgent, ct);
        var isAutonomous = trust.Level >= AgentTrustLevel.Act &&
                           trust.ApprovedSkills.Contains(context.Skill, StringComparer.OrdinalIgnoreCase);

        activity?.SetTag("rockbot.a2a.autonomous", isAutonomous);

        logger.LogInformation(
            "InputRequired trust check for '{TargetAgent}' skill={Skill}: level={Level}, autonomous={Autonomous}",
            context.TargetAgent, context.Skill, trust.Level, isAutonomous);

        // Derive session identifiers — PrimarySessionId is the full WM namespace
        // (e.g. "session/blazor-session"). Strip the prefix for conversation memory.
        var sessionNamespace = context.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        // Build the synthetic user turn depending on trust level
        var syntheticUserTurn = isAutonomous
            ? $"[Agent '{context.TargetAgent}' is requesting additional input for task {context.TaskId} (round {context.Round})]:\n" +
              $"Question: {context.QuestionText}\n\n" +
              $"Use your tools to generate the best response to send back to the agent."
            : $"[Agent '{context.TargetAgent}' needs input for task {context.TaskId} (round {context.Round})]:\n" +
              $"Question: {context.QuestionText}\n\n" +
              $"Please provide a response to send back to the agent on behalf of the user.";

        // For autonomous follow-ups (Act-level trust), don't publish intermediate
        // bubbles to the UI — the user only needs the final result. For non-autonomous
        // follow-ups, the question is surfaced through the conversation context.

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow)
            { AgentName = context.TargetAgent },
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        var batchId = Guid.NewGuid().ToString("N")[..12];
        var registryTools = toolRegistry.BuildAgentToolFunctions(sessionNamespace, batchId);

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sessionSkillTools.Tools,
                     ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        string responseText;
        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = rawSessionId,
                AgentName = DisplayName,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            responseText = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId,
                enableFollowUp: false, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", responseText, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to generate InputRequired response for task {TaskId}", context.TaskId);
            responseText = "I was unable to generate a response to this question.";
        }

        logger.LogInformation(
            "InputRequired response generated for task {TaskId} ({Len:N0} chars, autonomous={Autonomous})",
            context.TaskId, responseText.Length, isAutonomous);

        A2ADiagnostics.InputRequiredRounds.Add(1,
            new KeyValuePair<string, object?>("rockbot.a2a.target_agent", context.TargetAgent),
            new KeyValuePair<string, object?>("rockbot.a2a.autonomous", isAutonomous));

        activity?.SetStatus(ActivityStatusCode.Ok);

        return new InputRequiredResponse
        {
            ResponseText = responseText,
            WasAutonomous = isAutonomous
        };
    }
}

/// <summary>Input context for an InputRequired follow-up.</summary>
internal sealed record InputRequiredContext
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
    public required string TargetAgent { get; init; }
    public required string Skill { get; init; }
    public required string QuestionText { get; init; }
    public required string PrimarySessionId { get; init; }
    public required int Round { get; init; }
}

/// <summary>Result of InputRequired handling.</summary>
internal sealed record InputRequiredResponse
{
    public required string ResponseText { get; init; }
    public required bool WasAutonomous { get; init; }
}
