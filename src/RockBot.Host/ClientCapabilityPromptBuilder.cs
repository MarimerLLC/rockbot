using System.Text;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Translates a <see cref="ClientCapabilities"/> bitfield into a system-prompt
/// snippet that tells the agent what subset of formatting the receiving client
/// can render. Returns <c>null</c> when the capability set carries no opt-ins
/// beyond plain text — callers then skip prompt injection and the agent falls
/// back to its existing markdown-by-default behaviour. Lives agent-side; the
/// prompt text never crosses the bus.
/// </summary>
public static class ClientCapabilityPromptBuilder
{
    public static string? Build(ClientCapabilities caps)
    {
        // None / bare Text / unknown-bits-only → no special instructions.
        if ((caps & ClientCapabilityMasks.AnyMeaningful) == 0)
            return null;

        var allow = new List<string>(8);
        var deny = new List<string>(8);

        // ── Markdown subset ────────────────────────────────────────────────
        if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            allow.Add("**bold**, *italic*, `inline code`, and blockquotes");
        else
            deny.Add("any markdown formatting — emit plain text only");

        if (caps.HasFlag(ClientCapabilities.MarkdownHeadings))
            allow.Add("`#` / `##` / `###` headings");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("headings — the client renders `#` as a literal character");

        if (caps.HasFlag(ClientCapabilities.MarkdownTables))
            allow.Add("GFM-style tables (`| col | col |`)");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("tables — present tabular data as a bulleted or numbered list");

        if (caps.HasFlag(ClientCapabilities.MarkdownCode))
            allow.Add("fenced code blocks with a language hint (```` ```python ````)");

        if (caps.HasFlag(ClientCapabilities.LinkInline))
            allow.Add("inline links — `[text](https://...)`");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("`[text](url)` syntax — paste bare URLs so the client auto-links them");

        // ── Rich rendering ────────────────────────────────────────────────
        if (caps.HasFlag(ClientCapabilities.HtmlInline))
        {
            allow.Add(
                "a safe subset of inline HTML embedded in markdown for color or structure: " +
                "`<span style=\"color:#...\">…</span>`, `<table>`, `<details><summary>…</summary>…</details>`");
            deny.Add(
                "`<script>`, `<iframe>`, `<style>`, event handlers (`onclick`, `onerror`, …), " +
                "or external `<img src>` to untrusted hosts (the client sanitizer strips these anyway)");
        }

        if (caps.HasFlag(ClientCapabilities.SvgInline))
            allow.Add("inline `<svg>` for simple charts (no `<script>`, keep under ~500 lines)");

        if (caps.HasFlag(ClientCapabilities.ImageAttachment))
            allow.Add("image attachments (PNG/JPEG) when a rendered chart conveys more than prose");

        // ── Platform-native UI (future) ───────────────────────────────────
        var nativeUi = caps & ClientCapabilityMasks.NativeUi;
        if (nativeUi != 0)
            allow.Add($"platform-native UI primitives ({nativeUi}) — use the matching tool if available");

        // ── Assemble ──────────────────────────────────────────────────────
        var sb = new StringBuilder("Rendering capabilities of the client receiving your reply:");
        if (allow.Count > 0)
        {
            sb.AppendLine().Append("You MAY use:");
            foreach (var line in allow) sb.AppendLine().Append("- ").Append(line);
        }
        if (deny.Count > 0)
        {
            sb.AppendLine().Append("You MUST NOT use:");
            foreach (var line in deny) sb.AppendLine().Append("- ").Append(line);
        }
        sb.AppendLine().Append(
            "Plain markdown remains the default — reach for richer rendering only when it materially improves clarity.");

        return sb.ToString();
    }
}

internal static class ClientCapabilityMasks
{
    /// <summary>Every bit that, when set, changes the prompt output.</summary>
    public const ClientCapabilities AnyMeaningful =
        ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.HtmlInline |
        ClientCapabilities.SvgInline | ClientCapabilities.ImageAttachment |
        NativeUi;

    public const ClientCapabilities NativeUi =
        ClientCapabilities.DiscordEmbed | ClientCapabilities.SlackBlockKit |
        ClientCapabilities.TeamsAdaptiveCard;
}
