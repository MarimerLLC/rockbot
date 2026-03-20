namespace RockBot.Host;

/// <summary>
/// Configuration options for the agent host.
/// </summary>
public sealed class AgentHostOptions
{
    /// <summary>
    /// Topics the agent subscribes to.
    /// </summary>
    public List<string> Topics { get; } = [];

    /// <summary>
    /// Default maximum number of tool-calling round-trips per request.
    /// Individual models may override this via <c>ModelBehavior.MaxToolIterationsOverride</c>.
    /// Defaults to 50.
    /// </summary>
    public int MaxToolIterations { get; set; } = 50;

    /// <summary>
    /// Maximum number of times the completion evaluator can re-prompt the agent when it
    /// determines the task is incomplete. Set to 0 to disable completion evaluation entirely.
    /// Individual models may override this via <c>ModelBehavior.MaxCompletionRepromptsOverride</c>.
    /// Defaults to 1.
    /// </summary>
    public int MaxCompletionReprompts { get; set; } = 1;

    /// <summary>
    /// Maximum number of proactive follow-up passes the agent can take after completing
    /// the user's request. A follow-up pass lets the agent take additional helpful actions
    /// within the context of the conversation (e.g. looking up a contact, cross-referencing
    /// calendar events) without being explicitly asked. Set to 0 to disable.
    /// Individual models may override this via <c>ModelBehavior.MaxFollowUpPassesOverride</c>.
    /// Defaults to 1.
    /// </summary>
    public int MaxFollowUpPasses { get; set; } = 1;
}
