using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Slack.Services;

public sealed record WorkspaceInfo(string Name, string? Team, string? TeamId, string? User, string? Url);

public sealed record ConversationInfo(string Id, string Name, string Type, string? Topic, bool IsArchived);

public sealed record SlackMessage(
    string Ts,
    string Time,
    string? User,
    string Text,
    bool TextTruncated,
    int Replies,
    string? ThreadTs,
    IReadOnlyList<string> Files,
    string? Channel = null,
    string? Permalink = null);

public sealed record SlackPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record UserInfo(string Id, string? Name, string? RealName, string? Title, string? Email, string? TimeZone, bool IsBot);

public sealed record UnreadConversation(
    string Id, string Name, string Type, int UnreadCount, string LatestUnread, IReadOnlyList<SlackMessage> Messages);

public sealed record UnreadReport(
    string Workspace, string Since, IReadOnlyList<UnreadConversation> Conversations, int MoreConversations, bool ScanTruncated, string Note);

/// <summary>Read-only Slack operations with per-workspace user and channel-name caches.</summary>
public sealed partial class SlackClient(SlackApi api, IOptions<SlackOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan UserTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan ChannelTtl = TimeSpan.FromMinutes(10);
    private const int UnreadSearchPages = 5;    // 100 results per page
    private const int MaxUnreadChecks = 100;
    private const int UnreadParallelism = 5;
    private const int MaxChannelPages = 10;   // 200 per page: up to 2,000 channels

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    private readonly ConcurrentDictionary<(string, string), (string? Name, DateTimeOffset At)> _userNames = new();
    private readonly ConcurrentDictionary<string, (Dictionary<string, string> ByName, DateTimeOffset At)> _channels = new();
    private readonly ConcurrentDictionary<string, WorkspaceInfo> _workspaces = new();

    public async Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct)
    {
        var result = new List<WorkspaceInfo>();
        foreach (var w in api.Workspaces)
            result.Add(await WorkspaceInfoAsync(w.Name, ct));
        return result;
    }

    public async Task<SlackPage<ConversationInfo>> ListConversationsAsync(string workspace, IReadOnlyList<string> types, int top, string? cursor, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        using var doc = await api.CallAsync(workspace, "users.conversations",
        [
            ("types", string.Join(',', types)), ("exclude_archived", "true"), ("limit", top.ToString(CultureInfo.InvariantCulture)), ("cursor", cursor)
        ], ct);

        var items = new List<ConversationInfo>();
        foreach (var c in doc.RootElement.GetProperty("channels").EnumerateArray())
        {
            var type = Bool(c, "is_im") ? "dm" : Bool(c, "is_mpim") ? "group" : Bool(c, "is_private") ? "private" : "public";
            var name = type == "dm" ? "@" + (await UserNameAsync(workspace, Str(c, "user") ?? "", ct) ?? Str(c, "user")) : "#" + Str(c, "name");
            var topic = c.TryGetProperty("topic", out var t) ? Str(t, "value") : null;
            items.Add(new ConversationInfo(Str(c, "id")!, name, type, string.IsNullOrEmpty(topic) ? null : topic, Bool(c, "is_archived")));
        }
        return new SlackPage<ConversationInfo>(items, NextCursor(doc.RootElement));
    }

    public async Task<SlackPage<SlackMessage>> GetHistoryAsync(
        string workspace, string channel, DateTimeOffset? since, DateTimeOffset? until, int top, string? cursor, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        var id = await ResolveChannelAsync(workspace, channel, ct);
        using var doc = await api.CallAsync(workspace, "conversations.history",
        [
            ("channel", id), ("limit", top.ToString(CultureInfo.InvariantCulture)), ("cursor", cursor),
            ("oldest", since is null ? null : ToTs(since.Value)), ("latest", until is null ? null : ToTs(until.Value))
        ], ct);
        return new SlackPage<SlackMessage>(await MessagesAsync(workspace, doc.RootElement.GetProperty("messages"), null, ct), NextCursor(doc.RootElement));
    }

    public async Task<SlackPage<SlackMessage>> GetThreadAsync(string workspace, string channel, string ts, int top, string? cursor, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        var id = await ResolveChannelAsync(workspace, channel, ct);
        using var doc = await api.CallAsync(workspace, "conversations.replies",
            [("channel", id), ("ts", ts), ("limit", top.ToString(CultureInfo.InvariantCulture)), ("cursor", cursor)], ct);
        return new SlackPage<SlackMessage>(await MessagesAsync(workspace, doc.RootElement.GetProperty("messages"), null, ct), NextCursor(doc.RootElement));
    }

    public async Task<IReadOnlyList<SlackMessage>> SearchAsync(string workspace, string query, int top, string sort, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        using var doc = await api.CallAsync(workspace, "search.messages",
            [("query", query), ("count", top.ToString(CultureInfo.InvariantCulture)), ("sort", sort), ("sort_dir", "desc")], ct);
        var matches = doc.RootElement.TryGetProperty("messages", out var m) && m.TryGetProperty("matches", out var list) ? list : default;
        return matches.ValueKind == JsonValueKind.Array ? await MessagesAsync(workspace, matches, fromSearch: true, ct) : [];
    }

    /// <summary>
    /// Conversations with messages you haven't read. Slack exposes unread counts only for DMs, so this
    /// searches recent messages from other people (a few search calls rather than one history call
    /// per conversation), then compares each conversation's last-read mark with what it found.
    /// </summary>
    public async Task<UnreadReport> ListUnreadAsync(
        string workspace, int days, int maxConversations, int messagesPerConversation, IReadOnlySet<string> types, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        var since = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _zone).Date.AddDays(-days);
        // after: is exclusive, so search from the day before.
        var query = $"after:{since.AddDays(-1):yyyy-MM-dd} -from:me";

        var byConversation = new Dictionary<string, (JsonElement Channel, List<JsonElement> Messages)>();
        var truncated = false;
        for (var page = 1; page <= UnreadSearchPages; page++)
        {
            using var doc = await api.CallAsync(workspace, "search.messages",
                [("query", query), ("count", "100"), ("page", page.ToString(CultureInfo.InvariantCulture)), ("sort", "timestamp"), ("sort_dir", "desc")], ct);
            var messages = doc.RootElement.GetProperty("messages");
            foreach (var m in messages.TryGetProperty("matches", out var matches) ? matches.EnumerateArray() : default)
            {
                // Thread replies are tracked separately from the channel's last-read mark.
                if (Str(m, "permalink")?.Contains("thread_ts=", StringComparison.Ordinal) == true
                    || !m.TryGetProperty("channel", out var channel) || Str(channel, "id") is not { } id
                    || !types.Contains(TypeOf(channel)))
                    continue;
                if (!byConversation.TryGetValue(id, out var entry))
                    byConversation[id] = entry = (channel.Clone(), []);
                entry.Messages.Add(m.Clone());
            }
            var pages = messages.TryGetProperty("paging", out var paging) && paging.TryGetProperty("pages", out var p) && p.TryGetInt32(out var n) ? n : 1;
            if (page >= pages)
                break;
            truncated = page == UnreadSearchPages;
        }

        using var gate = new SemaphoreSlim(UnreadParallelism);
        var checks = byConversation.Take(MaxUnreadChecks).Select(async kv =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var info = await api.CallAsync(workspace, "conversations.info", [("channel", kv.Key)], ct);
                var c = info.RootElement.GetProperty("channel");
                var lastRead = TsValue(Str(c, "last_read"));
                var unread = kv.Value.Messages.Where(m => TsValue(Str(m, "ts")) > lastRead).ToList();
                if (unread.Count == 0)
                    return null;
                var count = c.TryGetProperty("unread_count_display", out var u) && u.TryGetInt32(out var shown) && shown > unread.Count ? shown : unread.Count;
                return new UnreadHit(kv.Key, kv.Value.Channel, unread, count);
            }
            catch (McpException)
            {
                return null;   // Left the channel or lost access since the message was indexed.
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var ordered = (await Task.WhenAll(checks)).OfType<UnreadHit>()
            .OrderByDescending(h => TsValue(Str(h.Unread[0], "ts"))).ToList();
        var result = new List<UnreadConversation>();
        foreach (var (id, channel, unread, count) in ordered.Take(maxConversations))
        {
            var type = TypeOf(channel);
            var name = type == "dm"
                ? "@" + (await UserNameAsync(workspace, Str(channel, "user") ?? Str(channel, "name") ?? "", ct) ?? Str(channel, "name"))
                : type == "group" ? Str(channel, "name") ?? id : "#" + Str(channel, "name");

            IReadOnlyList<SlackMessage> messages = [];
            if (messagesPerConversation > 0)
            {
                using var latest = JsonDocument.Parse("[" + string.Join(",", unread.Take(messagesPerConversation).Select(m => m.GetRawText())) + "]");
                messages = await MessagesAsync(workspace, latest.RootElement, fromSearch: true, ct);
            }
            result.Add(new UnreadConversation(id, name, type, count, FormatTs(Str(unread[0], "ts")!), messages));
        }

        return new UnreadReport(
            workspace,
            since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            result,
            Math.Max(0, ordered.Count - maxConversations),
            truncated || byConversation.Count > MaxUnreadChecks,
            $"Covers messages from other people since {since:yyyy-MM-dd} in conversations you can see; older unread messages " +
            "and unread thread replies are not included. Counts are what was found in that window.");
    }

    public async Task<UserInfo> GetUserAsync(string workspace, string userId, CancellationToken ct)
    {
        await WorkspaceInfoAsync(workspace, ct);
        using var doc = await api.CallAsync(workspace, "users.info", [("user", userId)], ct);
        var u = doc.RootElement.GetProperty("user");
        var profile = u.TryGetProperty("profile", out var p) ? p : default;
        string? P(string name) => profile.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(profile, name)) : null;
        return new UserInfo(
            Id: Str(u, "id")!,
            Name: P("display_name") ?? Str(u, "name"),
            RealName: P("real_name") ?? NullIfEmpty(Str(u, "real_name")),
            Title: P("title"),
            Email: P("email"),
            TimeZone: NullIfEmpty(Str(u, "tz")),
            IsBot: Bool(u, "is_bot"));
    }

    public string FormatTs(string ts) => ToLocal(ts).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public DateTimeOffset ToInstant(DateTime local) => new(local, _zone.GetUtcOffset(local));

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Checks the token once per workspace with auth.test, refusing one issued for a different team.</summary>
    private async Task<WorkspaceInfo> WorkspaceInfoAsync(string workspace, CancellationToken ct)
    {
        if (_workspaces.TryGetValue(workspace, out var cached))
            return cached;

        var expected = api.Workspace(workspace).TeamId;
        using var doc = await api.CallAsync(workspace, "auth.test", [], ct);
        var r = doc.RootElement;
        var teamId = Str(r, "team_id");
        if (expected.Length > 0 && teamId != expected)
            throw new McpException($"the token for workspace '{workspace}' belongs to team {teamId}, not {expected}; re-run deploy/slack/setup.sh");
        return _workspaces[workspace] = new WorkspaceInfo(workspace, Str(r, "team"), teamId, Str(r, "user"), Str(r, "url"));
    }

    private async Task<IReadOnlyList<SlackMessage>> MessagesAsync(string workspace, JsonElement messages, bool? fromSearch, CancellationToken ct)
    {
        var max = options.Value.MaxMessageChars;
        var result = new List<SlackMessage>();
        foreach (var m in messages.EnumerateArray())
        {
            var ts = Str(m, "ts") ?? "0";
            var userId = Str(m, "user");
            var user = fromSearch == true
                ? Str(m, "username") ?? (userId is null ? null : await UserNameAsync(workspace, userId, ct))
                : userId is null ? Str(m, "username") ?? (m.TryGetProperty("bot_profile", out var bot) ? Str(bot, "name") : null)
                    : await UserNameAsync(workspace, userId, ct);

            var text = await RenderAsync(workspace, Str(m, "text") ?? "", ct);
            var truncated = text.Length > max;
            var files = m.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => Str(x, "name") ?? Str(x, "title") ?? "file").ToList()
                : [];
            string? channel = null;
            if (fromSearch == true && m.TryGetProperty("channel", out var ch))
                channel = Bool(ch, "is_im") ? Str(ch, "id") : "#" + Str(ch, "name");

            result.Add(new SlackMessage(
                Ts: ts,
                Time: FormatTs(ts),
                User: user,
                Text: truncated ? text[..max] : text,
                TextTruncated: truncated,
                Replies: m.TryGetProperty("reply_count", out var rc) && rc.TryGetInt32(out var n) ? n : 0,
                ThreadTs: Str(m, "thread_ts") is { } thread && thread != ts ? thread : null,
                Files: files,
                Channel: channel,
                Permalink: Str(m, "permalink")));
        }
        return result;
    }

    /// <summary>Rewrites Slack's mrkdwn references (&lt;@U1&gt;, &lt;#C1|name&gt;, &lt;url|label&gt;) into readable text.</summary>
    internal async Task<string> RenderAsync(string workspace, string text, CancellationToken ct)
    {
        var names = new Dictionary<string, string?>();
        foreach (Match m in UserMention().Matches(text))
            names.TryAdd(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : await UserNameAsync(workspace, m.Groups[1].Value, ct));

        var rendered = UserMention().Replace(text, m => "@" + (names[m.Groups[1].Value] ?? m.Groups[1].Value));
        rendered = ChannelMention().Replace(rendered, m => "#" + (m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value));
        rendered = SpecialMention().Replace(rendered, m => "@" + (m.Groups[2].Success ? m.Groups[2].Value.TrimStart('@') : m.Groups[1].Value));
        rendered = Link().Replace(rendered, m => m.Groups[2].Success ? $"{m.Groups[2].Value} ({m.Groups[1].Value})" : m.Groups[1].Value);
        return rendered.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }

    private async Task<string?> UserNameAsync(string workspace, string userId, CancellationToken ct)
    {
        if (userId.Length == 0)
            return null;
        var now = clock.GetUtcNow();
        if (_userNames.TryGetValue((workspace, userId), out var hit) && now - hit.At < UserTtl)
            return hit.Name;

        string? name;
        try
        {
            var user = await GetUserAsync(workspace, userId, ct);
            name = user.Name ?? user.RealName;
        }
        catch (McpException)
        {
            name = null;   // A deleted or external user; fall back to the raw id.
        }
        _userNames[(workspace, userId)] = (name, now);
        return name;
    }

    /// <summary>A conversation id for an id or a '#name' / 'name' of a channel the user is in.</summary>
    private async Task<string> ResolveChannelAsync(string workspace, string channel, CancellationToken ct)
    {
        var value = channel.Trim();
        if (ChannelId().IsMatch(value))
            return value;

        var name = value.TrimStart('#').ToLowerInvariant();
        var now = clock.GetUtcNow();
        if (!_channels.TryGetValue(workspace, out var map) || now - map.At >= ChannelTtl || !map.ByName.ContainsKey(name))
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? cursor = null;
            var pages = 0;
            do
            {
                if (++pages > MaxChannelPages)
                    break;
                using var doc = await api.CallAsync(workspace, "users.conversations",
                    [("types", "public_channel,private_channel"), ("exclude_archived", "true"), ("limit", "200"), ("cursor", cursor)], ct);
                foreach (var c in doc.RootElement.GetProperty("channels").EnumerateArray())
                    if (Str(c, "name") is { } n)
                        byName[n] = Str(c, "id")!;
                cursor = NextCursor(doc.RootElement);
            } while (cursor is not null);
            map = (byName, now);
            _channels[workspace] = map;
        }

        return map.ByName.TryGetValue(name, out var id)
            ? id
            : throw new McpException($"no channel named '#{name}' that you are a member of in workspace '{workspace}'; use list_conversations");
    }

    private DateTimeOffset ToLocal(string ts)
    {
        var seconds = double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)), _zone);
    }

    private static string TypeOf(JsonElement channel) =>
        Bool(channel, "is_im") ? "dm" : Bool(channel, "is_mpim") ? "group" : Bool(channel, "is_private") ? "private" : "public";

    private sealed record UnreadHit(string Id, JsonElement Channel, List<JsonElement> Unread, int Count);

    private static double TsValue(string? ts) =>
        double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string ToTs(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string? NextCursor(JsonElement root) =>
        root.TryGetProperty("response_metadata", out var meta) && NullIfEmpty(Str(meta, "next_cursor")) is { } c ? c : null;

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    [GeneratedRegex(@"<@([UW][A-Z0-9]+)(?:\|([^>]+))?>")]
    private static partial Regex UserMention();

    [GeneratedRegex(@"<#([CGD][A-Z0-9]+)(?:\|([^>]*))?>")]
    private static partial Regex ChannelMention();

    [GeneratedRegex(@"<!(here|channel|everyone|subteam\^[A-Z0-9]+)(?:\|([^>]+))?>")]
    private static partial Regex SpecialMention();

    [GeneratedRegex(@"<((?:https?|mailto):[^|>]+)(?:\|([^>]+))?>")]
    private static partial Regex Link();

    [GeneratedRegex("^[CGD][A-Z0-9]{6,}$")]
    private static partial Regex ChannelId();
}
