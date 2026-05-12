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

    /// <summary>
    /// Name of the Kubernetes PVC to mount as the shared volume.
    /// When non-empty, ephemeral pods receive a volume mount at <see cref="SharedVolumePath"/>.
    /// </summary>
    public string SharedVolumeClaim { get; set; } = "";

    /// <summary>
    /// Mount path for the shared volume inside script pods.
    /// Exposed to scripts via the <c>ROCKBOT_SHARED_PATH</c> environment variable.
    /// </summary>
    public string SharedVolumePath { get; set; } = "/rockbot/shared";

    /// <summary>
    /// Optional POSIX group ID applied as the pod-level <c>fsGroup</c>. When set
    /// (non-null and &gt; 0), kubelet chgrp's the shared volume to this GID and
    /// adds group-rw to the mode, so script pods (which run as UID 1000) can
    /// share files with the agent (UID 999) and the shared-cleanup cronjob
    /// (UID 0). Must match the fsGroup configured on those other pods.
    /// </summary>
    public long? FsGroup { get; set; }
}
