namespace RockBot.UserProxy.Cli;

/// <summary>
/// Formats a one-line placeholder for an <see cref="AgentAttachment"/> the CLI can't render
/// inline. Shared by the plain and Spectre frontends so the wording stays consistent, e.g.
/// <c>[image: chart.png (image/png)]</c> or <c>[attachment: report.pdf (application/pdf)]</c>.
/// Always surfaces the shared-volume <see cref="AgentAttachment.Path"/> — the file you can
/// actually inspect — and prepends the friendly <see cref="AgentAttachment.FileName"/> when it
/// differs, e.g. <c>[image: Q3 chart.png (chart.png, image/png)]</c>.
/// </summary>
internal static class AttachmentPlaceholder
{
    public static string Render(AgentAttachment attachment)
    {
        var kind = attachment.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : "attachment";
        var hasFriendlyName = !string.IsNullOrWhiteSpace(attachment.FileName)
            && !string.Equals(attachment.FileName, attachment.Path, StringComparison.Ordinal);
        return hasFriendlyName
            ? $"[{kind}: {attachment.FileName} ({attachment.Path}, {attachment.Mime})]"
            : $"[{kind}: {attachment.Path} ({attachment.Mime})]";
    }
}
