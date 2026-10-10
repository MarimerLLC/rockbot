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
    /// Largest page <c>file_read</c> returns, in characters. Defaults to 64,000. A file that
    /// fits is returned whole. A larger file is returned one page at a time on whole-line
    /// boundaries, and each page ends with a footer naming the next offset.
    /// </summary>
    /// <remarks>
    /// <c>file_read</c> is exempt from the agent host's generic per-result cap
    /// (<c>AgentHost:ToolResultMaxChars</c>, 8,000 by default), so this is the effective limit
    /// on a single read. See issue #664.
    /// </remarks>
    public int FileReadMaxChars { get; set; } = 64_000;

    /// <summary>
    /// Directory, relative to <see cref="BasePath"/>, where downloaded files land: the MCP
    /// bridge's attachment store (<c>${ROCKBOT_SHARED_PATH}/attachments</c>) and inbound user
    /// attachments. Defaults to <c>attachments</c>. When <c>file_write</c> or <c>file_edit</c>
    /// is refused a write there by the filesystem, the error explains that the area is
    /// read-only for file tools and names the matching <c>drafts/</c> file (issue #677).
    /// Set it empty to disable that message.
    /// </summary>
    public string? AttachmentsDirectory { get; set; } = "attachments";

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
