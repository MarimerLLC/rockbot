using System.Globalization;

namespace RockBot.UserProxy;

/// <summary>
/// Shared rendering of the <see cref="ReplyOrigin"/> anchor preamble and its relative-time
/// formatting, so every frontend produces a consistent anchor and the (fiddly) "2h 14m ago"
/// logic lives in one tested place.
/// </summary>
public static class ReplyOriginFormatter
{
    /// <summary>
    /// Renders the anchor preamble for <paramref name="origin"/>, or null when there is no
    /// origin or it should be suppressed (the reply originated in the channel + session the
    /// user is currently viewing, so the anchor would just be noise).
    /// </summary>
    public static string? RenderAnchor(
        ReplyOrigin? origin, string? currentChannel, string? currentSessionId, DateTimeOffset now) =>
        RenderAnchor(origin, currentChannel, currentSessionId, now, TimeZoneInfo.Local);

    /// <summary>
    /// <see cref="RenderAnchor(ReplyOrigin?, string?, string?, DateTimeOffset)"/>, with the start time
    /// in <paramref name="zone"/>: the viewer's zone, for a front end that renders on a server whose
    /// own zone isn't the viewer's.
    /// </summary>
    public static string? RenderAnchor(
        ReplyOrigin? origin, string? currentChannel, string? currentSessionId, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (origin is null)
            return null;

        if (currentChannel is not null
            && string.Equals(origin.Channel, currentChannel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.SessionId, currentSessionId, StringComparison.Ordinal))
            return null;

        var localTime = TimeZoneInfo.ConvertTime(origin.StartedAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var ago = RelativeTime(origin.StartedAt, now, zone);
        var summary = string.IsNullOrWhiteSpace(origin.PromptSummary) ? "(no prompt)" : origin.PromptSummary;
        return $"↳ Re: \"{summary}\"\n   started {localTime} from {origin.Channel} · {ago}";
    }

    /// <summary>
    /// The anchor for a reply seen in <paramref name="viewingSessionId"/>, so a background result
    /// never shows without one (#841). With an <paramref name="origin"/>, the anchor
    /// <see cref="RenderAnchor(ReplyOrigin?, string?, string?, DateTimeOffset)"/> gives, suppressed
    /// when the origin is <paramref name="currentChannel"/> and the viewed session. Without one (an
    /// agent that restarted lost it, or the work had none), a reply from a session other than the
    /// viewed one names what produced it and its session, from what the reply carries. Null
    /// otherwise. A null <paramref name="viewingSessionId"/> means the reply's own session.
    /// </summary>
    public static string? RenderAnchor(
        ReplyOrigin? origin, ReplySource? source, string? replySessionId,
        string? currentChannel, string? viewingSessionId, DateTimeOffset now)
    {
        var viewing = viewingSessionId ?? replySessionId;
        if (origin is not null)
            return RenderAnchor(origin, currentChannel, viewing, now);

        if (string.IsNullOrEmpty(replySessionId) || SessionIdForms.AreSame(replySessionId, viewing))
            return null;

        return $"↳ From {DescribeSource(source?.Kind, source?.TaskId)} in another conversation\n" +
            $"   session \"{replySessionId}\"";
    }

    /// <summary>
    /// <paramref name="content"/> with the anchor for <paramref name="origin"/> above it as a markdown
    /// blockquote, as the Blazor UI and the AG-UI sample show it; <paramref name="content"/> alone
    /// when <see cref="RenderAnchor(ReplyOrigin?, string?, string?, DateTimeOffset)"/> gives no
    /// anchor. Used for live replies and for the same replies reloaded from history.
    /// </summary>
    public static string WithQuotedAnchor(
        string content, ReplyOrigin? origin, string? currentChannel, string? currentSessionId, DateTimeOffset now) =>
        Quote(content, RenderAnchor(origin, currentChannel, currentSessionId, now));

    /// <summary>
    /// <see cref="WithQuotedAnchor(string, ReplyOrigin?, string?, string?, DateTimeOffset)"/>, with the
    /// start time in <paramref name="zone"/>.
    /// </summary>
    public static string WithQuotedAnchor(
        string content, ReplyOrigin? origin, string? currentChannel, string? currentSessionId, DateTimeOffset now,
        TimeZoneInfo zone) =>
        Quote(content, RenderAnchor(origin, currentChannel, currentSessionId, now, zone));

    /// <summary>
    /// <paramref name="content"/> with the anchor
    /// <see cref="RenderAnchor(ReplyOrigin?, ReplySource?, string?, string?, string?, DateTimeOffset)"/>
    /// gives above it as a markdown blockquote; <paramref name="content"/> alone when it gives none.
    /// </summary>
    public static string WithQuotedAnchor(
        string content, ReplyOrigin? origin, ReplySource? source, string? replySessionId,
        string? currentChannel, string? viewingSessionId, DateTimeOffset now) =>
        Quote(content, RenderAnchor(origin, source, replySessionId, currentChannel, viewingSessionId, now));

    /// <summary>
    /// What produced a reply, for a sentence: "a subagent (task abc123)", "a scheduled task",
    /// "an A2A agent", "the agent". <paramref name="kind"/> is a <see cref="ReplySourceKind"/>.
    /// </summary>
    public static string DescribeSource(string? kind, string? taskId)
    {
        var what = kind switch
        {
            ReplySourceKind.Subagent => "a subagent",
            ReplySourceKind.Worker => "a worker",
            ReplySourceKind.Scheduled => "a scheduled task",
            ReplySourceKind.A2A => "an A2A agent",
            _ => "the agent",
        };
        if (string.IsNullOrWhiteSpace(taskId) || kind is not (ReplySourceKind.Subagent or ReplySourceKind.Worker))
            return what;
        var shortId = taskId.Length > 8 ? taskId[..8] : taskId;
        return $"{what} (task {shortId})";
    }

    private static string Quote(string content, string? anchor)
    {
        if (anchor is null)
            return content;
        var quoted = string.Join("\n", anchor.Split('\n').Select(line => "> " + line.TrimStart()));
        return quoted + "\n\n" + content;
    }

    /// <summary>Formats the gap between <paramref name="then"/> and <paramref name="now"/> as a coarse "ago" string.</summary>
    public static string RelativeTime(DateTimeOffset then, DateTimeOffset now) =>
        RelativeTime(then, now, TimeZoneInfo.Local);

    /// <summary>
    /// <see cref="RelativeTime(DateTimeOffset, DateTimeOffset)"/>, with a date more than a week back
    /// in <paramref name="zone"/>.
    /// </summary>
    public static string RelativeTime(DateTimeOffset then, DateTimeOffset now, TimeZoneInfo zone)
    {
        var span = now - then;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalSeconds < 60)
            return "just now";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)
        {
            var h = (int)span.TotalHours;
            var m = span.Minutes;
            return m > 0 ? $"{h}h {m}m ago" : $"{h}h ago";
        }
        if (span.TotalDays < 2)
            return "yesterday";
        if (span.TotalDays < 7)
            return $"{(int)span.TotalDays}d ago";
        return TimeZoneInfo.ConvertTime(then, zone).ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
