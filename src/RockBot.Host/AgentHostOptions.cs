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
    /// Defaults to 2.
    /// </summary>
    public int MaxCompletionReprompts { get; set; } = 2;
}
