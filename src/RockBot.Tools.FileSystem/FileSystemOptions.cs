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

    /// <summary>
    /// Largest file <c>analyze_file</c> will hand to a model, in bytes. Defaults to 8 MiB.
    /// Providers cap the encoded request well above this; the limit is here so that pointing
    /// the tool at the wrong file stays cheap.
    /// </summary>
    public long AnalyzeFileMaxBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>
    /// MIME types <c>analyze_file</c> is permitted to send. Defaults to the four image
    /// formats every vision-capable provider accepts. Adding <c>application/pdf</c> or an
    /// audio type is a deployment decision, because whether it works depends on the provider
    /// behind the configured tier.
    /// </summary>
    public IList<string> AnalyzeFileMimeTypes { get; set; } =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];
}
