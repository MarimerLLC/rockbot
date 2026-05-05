namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Filesystem facade for the shared attachments directory. The agent and script pods both
/// mount the same shared volume, so the gateway uses this abstraction to read files the
/// model has produced and to write files the gateway has fetched on the model's behalf.
/// </summary>
public interface IAttachmentStorage
{
    /// <summary>
    /// Absolute base path of the attachments directory (e.g. <c>/rockbot/shared/attachments</c>).
    /// </summary>
    string BasePath { get; }

    /// <summary>
    /// Reads the contents of an attachment file. <paramref name="path"/> may be a bare filename
    /// resolved relative to <see cref="BasePath"/> or an absolute path under <see cref="BasePath"/>.
    /// </summary>
    Task<byte[]> ReadAsync(string path, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="data"/> into <see cref="BasePath"/>, choosing a non-colliding
    /// filename derived from <paramref name="preferredFileName"/> (suffixes <c>-2</c>, <c>-3</c>,
    /// etc. when the preferred name already exists). Returns the resolved absolute path.
    /// </summary>
    Task<string> WriteAsync(string preferredFileName, byte[] data, CancellationToken ct);
}
