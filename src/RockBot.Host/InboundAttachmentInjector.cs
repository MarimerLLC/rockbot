using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Puts the files a user attached to the current turn in front of the model — as real image
/// content parts when the tier can see, and as a named path it can hand to <c>analyze_file</c>
/// when it cannot.
/// </summary>
/// <remarks>
/// On OpenAI-compatible APIs bytes can only enter a conversation as content parts on a user
/// message, which is why this appends to the last user message rather than adding one. See
/// <c>design/multimodal-input.md</c>.
///
/// <para>Only the current turn's attachments are materialised. Replayed history turns keep the
/// marker line naming their path, so the model can still reach an older image deliberately via
/// <c>analyze_file</c> without every past screenshot standing in context — and without a disk
/// read per image per request.</para>
///
/// <para>This lives in the host rather than the agent because the agent owns
/// <c>IAttachmentStorage</c> and the host cannot see it; the caller passes the read as a
/// delegate, which also makes the whole thing testable without a filesystem.</para>
/// </remarks>
public static class InboundAttachmentInjector
{
    /// <summary>
    /// Appends content parts for <paramref name="attachments"/> to the last user message in
    /// <paramref name="messages"/>. Does nothing when there are no attachments or no user
    /// message to attach them to.
    /// </summary>
    /// <param name="messages">The assembled context; the last user message is modified in place.</param>
    /// <param name="attachments">Attachments on the current turn, as (mime, path, fileName) tuples.</param>
    /// <param name="tierCanSee">Whether the tier this turn will run on accepts image input.</param>
    /// <param name="readBytesAsync">Reads an attachment's bytes given its path reference.</param>
    /// <param name="logger">Logger for read failures and for what was injected.</param>
    public static async Task InjectAsync(
        IList<ChatMessage> messages,
        IReadOnlyList<ConversationTurnAttachment> attachments,
        bool tierCanSee,
        Func<string, CancellationToken, Task<byte[]>> readBytesAsync,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (attachments is null || attachments.Count == 0)
            return;

        var target = FindLastUserMessage(messages);
        if (target is null)
        {
            logger.LogWarning(
                "{Count} attachment(s) could not be injected: no user message in the context.",
                attachments.Count);
            return;
        }

        foreach (var attachment in attachments)
        {
            var isImage = attachment.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            if (isImage && tierCanSee)
            {
                byte[] bytes;
                try
                {
                    bytes = await readBytesAsync(attachment.Path, ct);
                }
                catch (Exception ex)
                {
                    // One unreadable file must not fail the turn. Fall back to the marker so the
                    // model still knows a file was sent and can try to reach it itself.
                    logger.LogWarning(ex,
                        "Could not read attachment {Path}; describing it instead of showing it.",
                        attachment.Path);
                    target.Contents.Add(new TextContent(BuildMarker(attachment, unreadable: true)));
                    continue;
                }

                target.Contents.Add(new DataContent(bytes, attachment.Mime));
                logger.LogInformation(
                    "Injected image attachment {Path} ({Mime}, {Bytes:N0} bytes) into the user message.",
                    attachment.Path, attachment.Mime, bytes.Length);
                continue;
            }

            // Either the model cannot see, or this is not an image. Say so plainly and name the
            // path — silently dropping a file the user deliberately attached is the one outcome
            // that is never acceptable.
            target.Contents.Add(new TextContent(BuildMarker(attachment, unreadable: false)));
            logger.LogInformation(
                "Announced attachment {Path} ({Mime}) as a path reference (tierCanSee={CanSee}, isImage={IsImage}).",
                attachment.Path, attachment.Mime, tierCanSee, isImage);
        }
    }

    /// <summary>
    /// The line shown to the model for an attachment it cannot be handed directly. Names the
    /// path and the tool, because a model told only that "a file was attached" reliably either
    /// ignores it or invents its contents.
    /// </summary>
    public static string BuildMarker(ConversationTurnAttachment attachment, bool unreadable)
    {
        var display = string.IsNullOrWhiteSpace(attachment.FileName)
            ? attachment.Path
            : attachment.FileName;

        var reason = unreadable
            ? "it could not be read from the shared volume"
            : "it cannot be shown to you directly";

        return $"[The user attached {display} ({attachment.Mime}) at attachments/{attachment.Path}. " +
               $"You are seeing this as a note rather than the file itself because {reason}. " +
               $"Use analyze_file on that path to examine it.]";
    }

    private static ChatMessage? FindLastUserMessage(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
                return messages[i];
        }
        return null;
    }
}
