namespace RockBot.UserProxy;

/// <summary>
/// Result of an <see cref="AttachmentUploadRequest"/>. On success <see cref="Attachment"/> holds
/// the path reference to put on <see cref="UserMessage.Attachments"/>; on failure
/// <see cref="Error"/> explains why in words a person can act on (too large, type not accepted),
/// because the only place that answer can be authoritative is the agent.
/// </summary>
public sealed record AttachmentUploadResponse
{
    /// <summary>Whether the file was written.</summary>
    public required bool Success { get; init; }

    /// <summary>The stored file as a path reference. Null when <see cref="Success"/> is false.</summary>
    public AgentAttachment? Attachment { get; init; }

    /// <summary>Human-readable reason for a rejection. Null on success.</summary>
    public string? Error { get; init; }
}
