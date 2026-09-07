using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Validates and stores a file a person sent to the agent. The single door every inbound
/// attachment comes through, whichever transport carried it — the bus
/// (<see cref="AttachmentUploadHandler"/>) or HTTP (<see cref="AttachmentUploadEndpoint"/>).
/// </summary>
/// <remarks>
/// The two transports exist for different reasons and must not drift: HTTP keeps multi-megabyte
/// payloads off the broker entirely, and the bus keeps working for a client that cannot reach the
/// endpoint — a CLI on someone's laptop. They share this class so the allowlist, the size cap and
/// the extension check are one implementation rather than two that agree today.
/// </remarks>
internal sealed class InboundAttachmentService(
    IAttachmentStorage storage,
    ILogger<InboundAttachmentService> logger)
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

    /// <summary>Accepted MIME types, for an endpoint that wants to say what it takes.</summary>
    internal static IReadOnlyCollection<string> AcceptedMimeTypes =>
        [.. AllowedMimeExtensions.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Validates and writes the upload. A rejection is a normal outcome carrying a reason the
    /// person who picked the file can act on, not an exception.
    /// </summary>
    public async Task<AttachmentUploadResponse> StoreAsync(
        string? fileName, string? mime, byte[]? data, string sessionId, CancellationToken ct)
    {
        if (data is null || data.Length == 0)
            return Rejected("The file is empty.");

        if (data.Length > MaxBytes)
            return Rejected(
                $"The file is {data.Length / 1024 / 1024.0:F1} MB, over the {MaxBytes / 1024 / 1024} MB limit.");

        if (string.IsNullOrWhiteSpace(fileName))
            return Rejected("The file has no name.");

        if (!AllowedMimeExtensions.TryGetValue(mime ?? string.Empty, out var extensions))
            return Rejected(
                $"Files of type '{mime}' are not accepted. Accepted types: " +
                string.Join(", ", AcceptedMimeTypes) + ".");

        // A declared type that disagrees with the extension is either a mistake or an attempt to
        // get a file stored under a name that will later be read as something else.
        var extension = Path.GetExtension(fileName);
        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Rejected(
                $"'{fileName}' does not look like a {mime} file (expected {string.Join(" or ", extensions)}).");

        try
        {
            var fullPath = await storage.WriteAsync(fileName, data, ct);
            var storedName = Path.GetFileName(fullPath);

            logger.LogInformation(
                "Stored inbound attachment {Path} ({Mime}, {Bytes:N0} bytes) for session {SessionId}",
                storedName, mime, data.Length, sessionId);

            return new AttachmentUploadResponse
            {
                Success = true,
                Attachment = new AgentAttachment
                {
                    Mime = mime!,
                    // Relative, like every other attachment on the wire — the receiving side
                    // resolves it under its own base with a containment check.
                    Path = storedName,
                    // Preserve what the user called it; WriteAsync may have renamed to dodge a
                    // collision, and they should still see their own filename.
                    FileName = fileName,
                },
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write inbound attachment {FileName}", fileName);
            return Rejected("The file could not be saved. Try again.");
        }
    }

    private static AttachmentUploadResponse Rejected(string error) =>
        new() { Success = false, Error = error };
}
