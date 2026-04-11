namespace RockBot.Scripts.Docker;

/// <summary>
/// Configuration for Docker-based script execution.
/// </summary>
public sealed class DockerScriptOptions
{
    /// <summary>
    /// Container image for running Python scripts. Defaults to "python:3.12-slim".
    /// </summary>
    public string Image { get; set; } = "python:3.12-slim";

    /// <summary>
    /// CPU resource limit in Kubernetes-style notation (e.g. "500m" for half a CPU).
    /// Converted to Docker NanoCPUs internally.
    /// </summary>
    public string CpuLimit { get; set; } = "500m";

    /// <summary>
    /// Memory resource limit in Kubernetes-style notation (e.g. "256Mi").
    /// Converted to bytes internally.
    /// </summary>
    public string MemoryLimit { get; set; } = "256Mi";

    /// <summary>
    /// Docker network mode. Defaults to "bridge" for outbound internet access
    /// (scripts commonly interact with internet-based services).
    /// </summary>
    public string NetworkMode { get; set; } = "bridge";

    /// <summary>
    /// Default topic for publishing script results when no ReplyTo is set.
    /// </summary>
    public string DefaultResultTopic { get; set; } = "script.result";

    /// <summary>
    /// Docker volume name to mount as the shared volume.
    /// When non-empty, containers receive a bind mount at <see cref="SharedVolumePath"/>.
    /// </summary>
    public string SharedVolumeName { get; set; } = "";

    /// <summary>
    /// Mount path for the shared volume inside script containers.
    /// Exposed to scripts via the <c>ROCKBOT_SHARED_PATH</c> environment variable.
    /// </summary>
    public string SharedVolumePath { get; set; } = "/rockbot/shared";

    /// <summary>
    /// Parses <see cref="CpuLimit"/> to Docker NanoCPUs.
    /// "500m" → 500_000_000, "1" → 1_000_000_000.
    /// </summary>
    internal long GetNanoCpus()
    {
        var value = CpuLimit.Trim();
        if (value.EndsWith('m'))
            return long.Parse(value[..^1]) * 1_000_000;
        return (long)(double.Parse(value) * 1_000_000_000);
    }

    /// <summary>
    /// Parses <see cref="MemoryLimit"/> to bytes.
    /// "256Mi" → 268_435_456, "512Mi" → 536_870_912.
    /// </summary>
    internal long GetMemoryBytes()
    {
        var value = MemoryLimit.Trim();
        if (value.EndsWith("Mi"))
            return long.Parse(value[..^2]) * 1024 * 1024;
        if (value.EndsWith("Gi"))
            return long.Parse(value[..^2]) * 1024 * 1024 * 1024;
        if (value.EndsWith("Ki"))
            return long.Parse(value[..^2]) * 1024;
        return long.Parse(value);
    }
}
