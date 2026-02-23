namespace RockBot.Host;

/// <summary>
/// Classifies the quality signal carried by a <see cref="FeedbackEntry"/>.
/// </summary>
public enum FeedbackSignalType
{
    /// <summary>A user message was detected as correcting the agent.</summary>
    Correction,

    /// <summary>A tool call threw or returned an error result.</summary>
    ToolFailure,

    /// <summary>An LLM-produced evaluation of a completed session.</summary>
    SessionSummary,

    /// <summary>The user explicitly marked an agent reply as helpful (thumbs up).</summary>
    UserThumbsUp,

    /// <summary>The user explicitly marked an agent reply as unhelpful (thumbs down).</summary>
    UserThumbsDown
}
