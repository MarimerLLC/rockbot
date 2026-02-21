namespace RockBot.Scripts.Remote;

/// <summary>
/// Configuration for the <c>execute_python_script</c> tool exposed to the LLM.
/// Controls limits enforced before passing the request to the underlying runner.
/// </summary>
public sealed class ScriptToolOptions
{
    /// <summary>
    /// Maximum value the LLM may request for <c>timeout_seconds</c>.
    /// Any request exceeding this is silently clamped down.
    /// Defaults to 300 seconds (5 minutes).
    /// </summary>
    public int MaxTimeoutSeconds { get; set; } = 300;
}
