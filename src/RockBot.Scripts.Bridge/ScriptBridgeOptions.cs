namespace RockBot.Scripts.Bridge;

/// <summary>
/// Configuration options for the script bridge service.
/// </summary>
public sealed class ScriptBridgeOptions
{
    /// <summary>
    /// Agent name used as the source identifier in published messages
    /// and as the subscription queue name suffix. Defaults to "script-bridge".
    /// </summary>
    public string AgentName { get; set; } = "script-bridge";

    /// <summary>
    /// Default topic for publishing script results when no ReplyTo is set.
    /// Defaults to "script.result".
    /// </summary>
    public string DefaultResultTopic { get; set; } = "script.result";
}
