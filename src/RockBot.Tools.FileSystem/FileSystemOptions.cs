namespace RockBot.Tools.FileSystem;

/// <summary>
/// Configuration options for shared-volume file tools.
/// </summary>
public sealed class FileSystemOptions
{
    /// <summary>
    /// Root directory for file operations. All paths are resolved relative to this base.
    /// Defaults to <c>/rockbot/shared</c>.
    /// </summary>
    public string BasePath { get; set; } = "/rockbot/shared";
}
