using System.Globalization;
using RockBot.UserProxy.ChatState;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// What a chat bubble shows, worked out when the page renders, in the viewer's time zone. The chat
/// state is shared by every open page and lives on the server, whose zone (UTC in the cluster) is
/// not the viewer's, so nothing time-of-day goes into it.
/// </summary>
public static class MessageDisplay
{
    /// <summary>
    /// <paramref name="message"/>'s content, with its origin anchor above it as a markdown
    /// blockquote when it has an origin that isn't <paramref name="viewingSessionId"/> on this
    /// channel: "↳ Re: "…" started HH:mm from &lt;channel&gt; · &lt;ago&gt;", the start in
    /// <paramref name="zone"/> and the "ago" as of <paramref name="now"/>.
    /// </summary>
    public static string Content(ChatMessage message, string viewingSessionId, DateTimeOffset now, TimeZoneInfo zone) =>
        message.IsFromUser
            ? message.Content
            : ReplyOriginFormatter.WithQuotedAnchor(
                message.Content, message.Origin, BlazorUserFrontend.Channel, viewingSessionId, now, zone);

    /// <summary>
    /// <paramref name="message"/>'s content as a saved response keeps it: with the origin anchor
    /// <see cref="Content"/> would draw, but dated ("started Nov 16 09:55 from scheduled") rather
    /// than "· 10h ago", which would be wrong when the saved copy is read later.
    /// </summary>
    public static string ForSaving(ChatMessage message, string viewingSessionId, TimeZoneInfo zone) =>
        message.IsFromUser
            ? message.Content
            : ReplyOriginFormatter.WithQuotedDatedAnchor(
                message.Content, message.Origin, BlazorUserFrontend.Channel, viewingSessionId, zone);

    /// <summary>
    /// A bubble's timestamp in <paramref name="zone"/>: the time alone for a message from today
    /// there, with the date otherwise, so two replies at the same time of day (a daily scheduled
    /// task, say) don't read as one shown twice.
    /// </summary>
    public static string Time(DateTime timestampUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var format = at.Date == today ? "HH:mm:ss"
            : at.Year == today.Year ? "MMM d, HH:mm:ss"
            : "MMM d, yyyy, HH:mm:ss";
        return at.ToString(format, CultureInfo.InvariantCulture);
    }
}
