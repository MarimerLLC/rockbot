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
        ReplyOrigin? origin, string? currentChannel, string? currentSessionId, DateTimeOffset now)
    {
        if (origin is null)
            return null;

        if (currentChannel is not null
            && string.Equals(origin.Channel, currentChannel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.SessionId, currentSessionId, StringComparison.Ordinal))
            return null;

        var localTime = origin.StartedAt.ToLocalTime().ToString("HH:mm");
        var ago = RelativeTime(origin.StartedAt, now);
        var summary = string.IsNullOrWhiteSpace(origin.PromptSummary) ? "(no prompt)" : origin.PromptSummary;
        return $"↳ Re: \"{summary}\"\n   started {localTime} from {origin.Channel} · {ago}";
    }

    /// <summary>Formats the gap between <paramref name="then"/> and <paramref name="now"/> as a coarse "ago" string.</summary>
    public static string RelativeTime(DateTimeOffset then, DateTimeOffset now)
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
        return then.ToLocalTime().ToString("MMM d");
    }
}
