namespace RockBot.Scripts.Container;

/// <summary>
/// Configuration for container-based script execution.
/// </summary>
public sealed class ContainerScriptOptions
{
    /// <summary>
    /// Kubernetes namespace for ephemeral script pods. Defaults to "rockbot-scripts".
    /// </summary>
    public string Namespace { get; set; } = "rockbot-scripts";

    /// <summary>
    /// Container image for running Python scripts. Defaults to "python:3.12-slim".
    /// </summary>
    public string Image { get; set; } = "python:3.12-slim";

    /// <summary>
    /// CPU resource limit for script pods. Defaults to "500m".
    /// </summary>
    public string CpuLimit { get; set; } = "500m";

    /// <summary>
    /// Memory resource limit for script pods. Defaults to "256Mi".
    /// </summary>
    public string MemoryLimit { get; set; } = "256Mi";

    /// <summary>
    /// Default topic for publishing script results when no ReplyTo is set.
    /// </summary>
    public string DefaultResultTopic { get; set; } = "script.result";
}
