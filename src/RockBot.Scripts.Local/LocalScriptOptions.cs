namespace RockBot.Scripts.Local;

/// <summary>
/// Configuration for process-based local script execution.
/// No container infrastructure is required; scripts run as child processes
/// of the host with the host Python interpreter.
/// </summary>
public sealed class LocalScriptOptions
{
    /// <summary>
    /// Path to the Python executable. Defaults to "python3" (falls back to "python" on Windows).
    /// </summary>
    public string PythonExecutable { get; set; } = OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// Working directory for script execution. When null, a temporary directory is
    /// created per execution and deleted on completion.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Default timeout in seconds for scripts that do not specify their own.
    /// Defaults to 30 seconds.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum timeout in seconds. Any script request exceeding this is clamped down.
    /// Defaults to 300 seconds (5 minutes).
    /// </summary>
    public int MaxTimeoutSeconds { get; set; } = 300;
}
