namespace RockBot.UserProxy;

/// <summary>
/// Hands a file's bytes to the agent so it can be written to the shared attachments directory
/// on the sender's behalf. The response carries back an <see cref="AgentAttachment"/> path
/// reference, which is what the eventual <see cref="UserMessage.Attachments"/> carries — the
/// conversation itself never carries bytes.
/// </summary>
/// <remarks>
/// The agent owns every write to the shared volume (frontends co-mount it read-only), so a
/// client cannot stage a file itself. Routing the upload through the agent also means the
/// containment check, filename sanitisation, MIME allowlist and size cap live in exactly one
/// place, and that a client with no access to the volume at all — the CLI on someone's laptop —
/// can still attach a file.
///
/// <para>This is the one place bytes cross the bus. It is deliberately its own request/reply
/// rather than a field on <see cref="UserMessage"/>, so the conversation message, the persisted
/// turn, and every history replay stay byte-free.</para>
/// </remarks>
public sealed record AttachmentUploadRequest
{
    /// <summary>Original filename as the user knows it; the agent sanitises it before writing.</summary>
    public required string FileName { get; init; }

    /// <summary>Declared MIME type. Checked against both the allowlist and the extension.</summary>
    public required string Mime { get; init; }

    /// <summary>File contents. Capped agent-side; see <c>AttachmentUploadOptions.MaxBytes</c>.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Session the upload belongs to, for logging and correlation.</summary>
    public required string SessionId { get; init; }

    /// <summary>User who supplied the file, for logging.</summary>
    public required string UserId { get; init; }
}
