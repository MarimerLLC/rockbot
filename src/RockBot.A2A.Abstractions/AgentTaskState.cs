namespace RockBot.A2A;

/// <summary>
/// Lifecycle state of an agent-to-agent task.
/// </summary>
public enum AgentTaskState
{
    Submitted,
    Working,
    InputRequired,
    Completed,
    Failed,
    Canceled
}
