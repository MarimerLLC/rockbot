namespace RockBot.Observation;

/// <summary>
/// Built-in <see cref="ITranscriptFilter"/> implementations. Targets pick the
/// filter that matches what they observe; a target observing the agent's
/// behaviour wants the everything filter, while a target observing the user
/// wants only user-authored turns plus the agent's user-facing replies.
/// </summary>
public static class TranscriptFilters
{
    /// <summary>
    /// Filter that passes every turn through unchanged. Use for targets like
    /// theory-of-self that observe the full trajectory regardless of who
    /// authored what.
    /// </summary>
    public static ITranscriptFilter Everything { get; } = new EverythingFilter();

    /// <summary>
    /// Filter that keeps only user-authored turns and the agent's
    /// user-facing replies. Excludes scheduled-task and heartbeat activity
    /// (the user never sees those — they aren't user signal) and excludes
    /// any tool-call / tool-result turns.
    /// </summary>
    /// <remarks>
    /// "User-facing reply" = a turn with <see cref="TranscriptTurn.Source"/>
    /// equal to <see cref="TranscriptSources.Agent"/> and
    /// <see cref="TranscriptTurn.Role"/> equal to <c>"assistant"</c>. Tool
    /// calls and tool results are stamped with different roles by the host
    /// adapter, so they fall through.
    /// </remarks>
    public static ITranscriptFilter UserAuthored { get; } = new UserAuthoredFilter();

    private sealed class EverythingFilter : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }

    private sealed class UserAuthoredFilter : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns)
        {
            foreach (var t in turns)
            {
                if (t.Source == TranscriptSources.User)
                    yield return t;
                else if (t.Source == TranscriptSources.Agent &&
                         string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    yield return t;
            }
        }
    }
}
