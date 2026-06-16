namespace RockBot.UserProxy;

/// <summary>
/// A binary payload attached to an <see cref="AgentReply"/>, carried as a path reference
/// rather than inline bytes. <see cref="Path"/> names a file the agent has produced (via a
/// script or MCP tool) under the shared attachments directory
/// (<c>${ROCKBOT_SHARED_PATH}/attachments</c>) — the same convention the MCP attachment
/// gateway speaks. Bytes never ride the bus: a capable frontend (e.g. Blazor) co-mounts the
/// shared volume and serves the file from its own endpoint; non-image frontends render a
/// placeholder.
/// </summary>
public sealed record AgentAttachment
{
    /// <summary>MIME type of the attachment (e.g. <c>image/png</c>).</summary>
    public required string Mime { get; init; }

    /// <summary>
    /// Bare filename or relative path under the shared attachments directory (e.g.
    /// <c>chart.png</c>). Never an absolute filesystem path on the wire — the receiving
    /// frontend resolves it under its own attachments base with a containment check.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Optional human-friendly display name; falls back to the leaf of <see cref="Path"/>.</summary>
    public string? FileName { get; init; }
}
