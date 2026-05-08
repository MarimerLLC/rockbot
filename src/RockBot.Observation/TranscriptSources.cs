namespace RockBot.Observation;

/// <summary>
/// Conventional values for <see cref="TranscriptTurn.Source"/>. The host's
/// conversation-log adapter is responsible for stamping these correctly; the
/// framework's filters use them to scope what each target observes.
/// Hosts may use additional values (e.g. "subagent", "a2a-call"); filters
/// that don't recognise a source should default to excluding it.
/// </summary>
public static class TranscriptSources
{
    /// <summary>Turn authored directly by the user (human input).</summary>
    public const string User = "user";

    /// <summary>Turn authored by the agent's LLM during a normal conversation.</summary>
    public const string Agent = "agent";

    /// <summary>Turn produced by a scheduled-task trigger (the agent acting on its own initiative on a cron).</summary>
    public const string ScheduledTask = "scheduled-task";

    /// <summary>Turn produced by the heartbeat patrol (periodic self-check).</summary>
    public const string Heartbeat = "heartbeat";
}
