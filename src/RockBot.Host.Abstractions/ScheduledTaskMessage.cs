namespace RockBot.Host;

/// <summary>
/// Message dispatched through the agent pipeline when a scheduled task fires.
/// </summary>
/// <param name="TaskName">Name of the scheduled task that fired.</param>
/// <param name="Description">Task description — the agent's instructions for this run.</param>
public sealed record ScheduledTaskMessage(string TaskName, string Description);
