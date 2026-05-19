using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Message dispatched through the agent pipeline when a scheduled task fires.
/// </summary>
/// <param name="TaskName">Name of the scheduled task that fired.</param>
/// <param name="Description">Task description — the agent's instructions for this run.</param>
/// <param name="IsSystemTask">When true the task is a system-internal background task.</param>
/// <param name="ClientCapabilities">
/// Rendering capabilities authored at schedule time. Propagated from
/// <see cref="ScheduledTask.ClientCapabilities"/> so the handler does not need to
/// re-fetch from the store on every fire.
/// </param>
public sealed record ScheduledTaskMessage(
    string TaskName,
    string Description,
    bool IsSystemTask = false,
    ClientCapabilities ClientCapabilities = ClientCapabilities.None);
