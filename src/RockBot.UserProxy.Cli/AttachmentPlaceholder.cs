namespace RockBot.UserProxy.Cli;

/// <summary>
/// Formats a one-line placeholder for an <see cref="AgentAttachment"/> the CLI can't render
/// inline. Shared by the plain and Spectre frontends so the wording stays consistent, e.g.
/// <c>[image: chart.png (image/png)]</c> or <c>[attachment: report.pdf (application/pdf)]</c>.
/// </summary>
internal static class AttachmentPlaceholder
{
    public static string Render(AgentAttachment attachment)
    {
        var label = string.IsNullOrWhiteSpace(attachment.FileName) ? attachment.Path : attachment.FileName;
        var kind = attachment.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : "attachment";
        return $"[{kind}: {label} ({attachment.Mime})]";
    }
}
