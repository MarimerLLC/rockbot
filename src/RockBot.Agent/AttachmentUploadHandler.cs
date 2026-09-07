using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="AttachmentUploadRequest"/> by writing the supplied bytes into the shared
/// attachments directory and replying with an <see cref="AgentAttachment"/> path reference.
/// </summary>
/// <remarks>
/// This is the inbound half of the attachment story, and it lives agent-side on purpose. The
/// frontends co-mount the shared volume read-only ("the agent owns writes") and the CLI does not
/// mount it at all, so a client cannot stage a file itself. Doing the write here also means the
/// allowlist, the size cap and the filename sanitisation exist once rather than once per client —
/// and a client can never talk the agent into writing somewhere else, because it never supplies a
/// path, only a name that <see cref="IAttachmentStorage.WriteAsync"/> sanitises.
/// </remarks>
internal sealed class AttachmentUploadHandler(
    IAttachmentStorage storage,
    IMessagePublisher publisher,
    ILogger<AttachmentUploadHandler> logger) : IMessageHandler<AttachmentUploadRequest>
{
    /// <summary>
    /// Largest upload accepted, matching <c>FileSystemOptions.AnalyzeFileMaxBytes</c> — a file
    /// the agent could not hand to a vision model anyway is not worth storing.
    /// </summary>
    internal const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>
    /// What a person may send. Deliberately narrower than the MIME table the outbound gateway
    /// knows: these are the types the agent can actually do something with — images it can see,
    /// and PDFs <c>analyze_file</c> can read. Matches <c>AnalyzeFileToolExecutor</c>'s own list.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedMimeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = [".png"],
            ["image/jpeg"] = [".jpg", ".jpeg"],
            ["image/gif"] = [".gif"],
            ["image/webp"] = [".webp"],
            ["image/bmp"] = [".bmp"],
            ["application/pdf"] = [".pdf"],
        };

    public async Task HandleAsync(AttachmentUploadRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("AttachmentUploadRequest received with no replyTo — ignoring");
            return;
        }

        var response = await StoreAsync(message, ct);

        if (response.Success)
        {
            logger.LogInformation(
                "Stored inbound attachment {Path} ({Mime}, {Bytes:N0} bytes) for session {SessionId}",
                response.Attachment!.Path, response.Attachment.Mime, message.Data.Length, message.SessionId);
        }
        else
        {
            logger.LogWarning(
                "Rejected inbound attachment {FileName} ({Mime}, {Bytes:N0} bytes) for session {SessionId}: {Error}",
                message.FileName, message.Mime, message.Data?.Length ?? 0, message.SessionId, response.Error);
        }

        var envelope = response.ToEnvelope<AttachmentUploadResponse>(
            source: context.Agent.Name,
            correlationId: context.Envelope.CorrelationId,
            replyTo: null,
            destination: null);

        await publisher.PublishAsync(replyTo, envelope, ct);
    }

    /// <summary>
    /// Validates and writes the upload. Split out from the message plumbing so the rules can be
    /// tested without a bus. A rejection is a normal outcome with a reason the user can act on,
    /// not an exception.
    /// </summary>
    internal async Task<AttachmentUploadResponse> StoreAsync(
        AttachmentUploadRequest message, CancellationToken ct)
    {
        if (message.Data is null || message.Data.Length == 0)
            return Rejected("The file is empty.");

        if (message.Data.Length > MaxBytes)
            return Rejected(
                $"The file is {message.Data.Length / 1024 / 1024.0:F1} MB, over the " +
                $"{MaxBytes / 1024 / 1024} MB limit.");

        if (string.IsNullOrWhiteSpace(message.FileName))
            return Rejected("The file has no name.");

        if (!AllowedMimeExtensions.TryGetValue(message.Mime ?? string.Empty, out var extensions))
            return Rejected(
                $"Files of type '{message.Mime}' are not accepted. Accepted types: " +
                string.Join(", ", AllowedMimeExtensions.Keys.Order(StringComparer.Ordinal)) + ".");

        // A declared type that disagrees with the extension is either a mistake or an attempt to
        // get a file stored under a name that will later be read as something else.
        var extension = Path.GetExtension(message.FileName);
        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Rejected(
                $"'{message.FileName}' does not look like a {message.Mime} file " +
                $"(expected {string.Join(" or ", extensions)}).");

        try
        {
            var fullPath = await storage.WriteAsync(message.FileName, message.Data, ct);
            var storedName = Path.GetFileName(fullPath);

            return new AttachmentUploadResponse
            {
                Success = true,
                Attachment = new AgentAttachment
                {
                    Mime = message.Mime!,
                    // Relative, like every other attachment on the wire — the receiving side
                    // resolves it under its own base with a containment check.
                    Path = storedName,
                    // Preserve what the user called it; WriteAsync may have renamed to dodge a
                    // collision, and they should still see their own filename.
                    FileName = message.FileName,
                },
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write inbound attachment {FileName}", message.FileName);
            return Rejected("The file could not be saved. Try again.");
        }
    }

    private static AttachmentUploadResponse Rejected(string error) =>
        new() { Success = false, Error = error };
}
