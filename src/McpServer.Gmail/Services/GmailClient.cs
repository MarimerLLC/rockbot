using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using ModelContextProtocol;

namespace McpServer.Gmail.Services;

public sealed record AccountInfo(string Name, string Email);

public sealed record MessageSummary(
    string Id, string ThreadId, string? From, string? To, string? Subject, string? Date, string? Snippet,
    IReadOnlyList<string> Labels, bool IsUnread);

public sealed record SummaryPage(IReadOnlyList<MessageSummary> Messages, string? NextPageToken, int? ResultSizeEstimate);

public sealed record AttachmentInfo(string Id, string Name, string? ContentType, long Size, bool IsInline);

public sealed record MessageDetail(
    string Id, string ThreadId, string? From, IReadOnlyList<string> To, IReadOnlyList<string> Cc, string? Subject,
    string? Date, IReadOnlyList<string> Labels, bool IsUnread, string Body, bool BodyTruncated, IReadOnlyList<AttachmentInfo> Attachments);

public sealed record ThreadDetail(string Id, IReadOnlyList<MessageDetail> Messages);

public sealed record LabelInfo(string Id, string Name, string Type);

/// <summary>An attachment: <see cref="Text"/> for text files, <see cref="Content"/> (base64) for binary ones, which the bridge saves.</summary>
public sealed record AttachmentContent(
    string Id,
    string Name,
    string? ContentType,
    long Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool TextTruncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null);

public sealed record DraftResult(string DraftId, string MessageId, string? ThreadId, string Link, string Note);

public sealed record DraftFields(
    IReadOnlyList<string>? To, IReadOnlyList<string>? Cc, IReadOnlyList<string>? Bcc, string? Subject, string? Body);

/// <summary>Gmail reads and drafts. There is deliberately no send, delete, trash or label change.</summary>
public sealed partial class GmailClient(GmailApi api, IOptions<GmailOptions> options)
{
    private const int Parallelism = 8;
    private const string MetadataHeaders = "metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date";

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/csv", "application/yaml", "application/x-yaml",
        "message/rfc822", "image/svg+xml", "application/ics"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".ics", ".vcf", ".yaml", ".yml", ".eml", ".html", ".htm", ".svg"
    };

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public IReadOnlyList<AccountInfo> ListAccounts() => api.Accounts.Select(a => new AccountInfo(a.Name, a.Email)).ToList();

    public Task<SummaryPage> SearchAsync(string account, string query, int top, string? pageToken, CancellationToken ct) =>
        ListAsync(account, [("q", query)], top, pageToken, ct);

    public Task<SummaryPage> ListByLabelAsync(string account, string label, bool unreadOnly, int top, string? pageToken, CancellationToken ct) =>
        ListAsync(account, unreadOnly ? [("labelIds", label), ("q", "is:unread")] : [("labelIds", label)], top, pageToken, ct);

    public async Task<MessageDetail> GetMessageAsync(string account, string id, CancellationToken ct)
    {
        using var doc = await api.GetAsync(account, $"messages/{id}?format=full", ct);
        return ToDetail(doc.RootElement, options.Value.MaxBodyChars);
    }

    public async Task<ThreadDetail> GetThreadAsync(string account, string threadId, CancellationToken ct)
    {
        using var doc = await api.GetAsync(account, $"threads/{threadId}?format=full", ct);
        var messages = doc.RootElement.TryGetProperty("messages", out var m) ? m.EnumerateArray().ToList() : [];
        // Share the body budget across the thread, keeping at least 2,000 characters per message.
        var perMessage = Math.Max(2_000, options.Value.MaxBodyChars / Math.Max(1, messages.Count));
        return new ThreadDetail(threadId, messages.Select(x => ToDetail(x, perMessage)).ToList());
    }

    public async Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(string account, CancellationToken ct)
    {
        using var doc = await api.GetAsync(account, "labels", ct);
        return doc.RootElement.TryGetProperty("labels", out var labels)
            ? labels.EnumerateArray()
                .Select(l => new LabelInfo(Str(l, "id")!, Str(l, "name") ?? "", Str(l, "type") ?? "user"))
                .OrderBy(l => l.Type == "system" ? 0 : 1).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    /// <summary>Downloads one attachment, found by its part id (stable, unlike Gmail's per-fetch attachment ids).</summary>
    public async Task<AttachmentContent> GetAttachmentAsync(string account, string messageId, string partId, CancellationToken ct)
    {
        using var doc = await api.GetAsync(account, $"messages/{messageId}?format=full", ct);
        var part = Parts(doc.RootElement.GetProperty("payload")).FirstOrDefault(p => Str(p, "partId") == partId);
        if (part.ValueKind != JsonValueKind.Object)
            throw new McpException($"message {messageId} has no attachment with id {partId}; use get_message to list them");

        var name = NullIfEmpty(Str(part, "filename")) ?? $"part-{partId}";
        var contentType = Str(part, "mimeType");
        var body = part.GetProperty("body");
        var size = body.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
        var max = options.Value.MaxAttachmentBytes;
        if (size > max)
            throw new McpException($"'{name}' is {size / 1_048_576.0:F1} MB, over the {max / 1_048_576} MB attachment limit");

        byte[] bytes;
        if (Str(body, "data") is { } inline)
            bytes = Base64Url(inline);
        else if (Str(body, "attachmentId") is { } attachmentId)
        {
            using var att = await api.GetAsync(account, $"messages/{messageId}/attachments/{attachmentId}", ct);
            bytes = Base64Url(Str(att.RootElement, "data") ?? "");
        }
        else
            bytes = [];

        var result = new AttachmentContent(partId, name, contentType, bytes.LongLength);
        if (!IsText(contentType, name))
            return result with { Content = Convert.ToBase64String(bytes) };

        var text = Decode(bytes, Header(part, "Content-Type"));
        var limit = options.Value.MaxBodyChars;
        return text.Length > limit ? result with { Text = text[..limit], TextTruncated = true } : result with { Text = text };
    }

    public async Task<DraftResult> CreateDraftAsync(
        string account, DraftFields fields, string? replyToMessageId, bool replyAll, CancellationToken ct)
    {
        var me = api.Account(account).Email;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(me));
        string? threadId = null;

        if (replyToMessageId is not null)
        {
            using var doc = await api.GetAsync(account,
                $"messages/{replyToMessageId}?format=metadata&metadataHeaders=Message-ID&metadataHeaders=References&metadataHeaders=Subject" +
                "&metadataHeaders=From&metadataHeaders=Reply-To&metadataHeaders=To&metadataHeaders=Cc", ct);
            var original = doc.RootElement;
            threadId = Str(original, "threadId");
            var payload = original.GetProperty("payload");

            if (Header(payload, "Message-ID") is { } originalId)
            {
                message.InReplyTo = originalId.Trim().Trim('<', '>');
                foreach (var reference in MimeUtils.EnumerateReferences(Header(payload, "References") ?? ""))
                    message.References.Add(reference);
                message.References.Add(message.InReplyTo);
            }

            var subject = Header(payload, "Subject") ?? "";
            message.Subject = fields.Subject ?? (ReplyPrefix().IsMatch(subject) ? subject : $"Re: {subject}");

            var from = Header(payload, "Reply-To") ?? Header(payload, "From") ?? "";
            var fromIsMe = Addresses(from).Any(a => IsMe(a, me));
            var defaultTo = fromIsMe ? Addresses(Header(payload, "To") ?? "") : Addresses(from);
            message.To.AddRange(fields.To is null ? defaultTo : Parse(fields.To, "to"));
            if (replyAll && fields.Cc is null)
            {
                var others = Addresses(Header(payload, "To") ?? "").Concat(Addresses(Header(payload, "Cc") ?? ""))
                    .Where(a => !IsMe(a, me) && !message.To.Mailboxes.Any(t => SameAddress(t, a)))
                    .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
                message.Cc.AddRange(others);
            }
        }
        else
        {
            message.To.AddRange(Parse(fields.To ?? [], "to"));
            message.Subject = fields.Subject ?? "";
        }

        if (fields.Cc is not null)
            message.Cc.AddRange(Parse(fields.Cc, "cc"));
        if (fields.Bcc is not null)
            message.Bcc.AddRange(Parse(fields.Bcc, "bcc"));
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
            throw new McpException("give at least one recipient in to");
        message.Body = new TextPart("plain") { Text = fields.Body ?? "" };

        using var created = await api.SendAsync(account, HttpMethod.Post, "drafts",
            new { message = new { raw = Raw(message), threadId } }, ct) ?? throw new McpException("Gmail returned no draft");
        return ToDraftResult(account, created.RootElement);
    }

    public async Task<DraftResult> UpdateDraftAsync(string account, string draftId, DraftFields fields, CancellationToken ct)
    {
        using var existing = await api.GetAsync(account, $"drafts/{draftId}?format=raw", ct);
        var current = existing.RootElement.GetProperty("message");
        var message = MimeMessage.Load(new MemoryStream(Base64Url(Str(current, "raw") ?? "")));

        if (fields.To is not null)
            Replace(message.To, Parse(fields.To, "to"));
        if (fields.Cc is not null)
            Replace(message.Cc, Parse(fields.Cc, "cc"));
        if (fields.Bcc is not null)
            Replace(message.Bcc, Parse(fields.Bcc, "bcc"));
        if (fields.Subject is not null)
            message.Subject = fields.Subject;
        if (fields.Body is not null)
            message.Body = new TextPart("plain") { Text = fields.Body };

        using var updated = await api.SendAsync(account, HttpMethod.Put, $"drafts/{draftId}",
            new { id = draftId, message = new { raw = Raw(message), threadId = Str(current, "threadId") } }, ct)
            ?? throw new McpException("Gmail returned no draft");
        return ToDraftResult(account, updated.RootElement);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<SummaryPage> ListAsync(
        string account, IEnumerable<(string Key, string Value)> filter, int top, string? pageToken, CancellationToken ct)
    {
        var query = filter.Select(p => (Key: p.Key, Value: p.Value)).Append((Key: "maxResults", Value: top.ToString(CultureInfo.InvariantCulture)));
        if (pageToken is not null)
            query = query.Append((Key: "pageToken", Value: pageToken));
        using var doc = await api.GetAsync(account, "messages?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")), ct);
        var root = doc.RootElement;
        var ids = root.TryGetProperty("messages", out var list)
            ? list.EnumerateArray().Select(m => Str(m, "id")!).ToList()
            : [];

        using var gate = new SemaphoreSlim(Parallelism);
        var summaries = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var m = await api.GetAsync(account, $"messages/{id}?format=metadata&{MetadataHeaders}", ct);
                return ToSummary(m.RootElement);
            }
            finally
            {
                gate.Release();
            }
        }));

        return new SummaryPage(summaries, Str(root, "nextPageToken"),
            root.TryGetProperty("resultSizeEstimate", out var est) && est.TryGetInt32(out var e) ? e : null);
    }

    private MessageSummary ToSummary(JsonElement m)
    {
        var payload = m.TryGetProperty("payload", out var p) ? p : default;
        var labels = Labels(m);
        return new MessageSummary(
            Id: Str(m, "id")!,
            ThreadId: Str(m, "threadId") ?? "",
            From: Header(payload, "From"),
            To: Header(payload, "To"),
            Subject: Header(payload, "Subject"),
            Date: FormatInternalDate(m),
            Snippet: Str(m, "snippet") is { } s ? WebUtility.HtmlDecode(s) : null,
            Labels: labels,
            IsUnread: labels.Contains("UNREAD"));
    }

    private MessageDetail ToDetail(JsonElement m, int maxBody)
    {
        var payload = m.GetProperty("payload");
        var parts = Parts(payload).ToList();
        var body = BodyText(parts);
        var truncated = body.Length > maxBody;
        var labels = Labels(m);

        var attachments = parts
            .Where(x => NullIfEmpty(Str(x, "filename")) is not null)
            .Select(x =>
            {
                var disposition = Header(x, "Content-Disposition") ?? "";
                var size = x.TryGetProperty("body", out var b) && b.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
                return new AttachmentInfo(Str(x, "partId")!, Str(x, "filename")!, Str(x, "mimeType"), size,
                    disposition.StartsWith("inline", StringComparison.OrdinalIgnoreCase) && Header(x, "Content-ID") is not null);
            })
            .ToList();

        return new MessageDetail(
            Id: Str(m, "id")!,
            ThreadId: Str(m, "threadId") ?? "",
            From: Header(payload, "From"),
            To: Addresses(Header(payload, "To") ?? "").Select(Format).ToList(),
            Cc: Addresses(Header(payload, "Cc") ?? "").Select(Format).ToList(),
            Subject: Header(payload, "Subject"),
            Date: FormatInternalDate(m),
            Labels: labels,
            IsUnread: labels.Contains("UNREAD"),
            Body: truncated ? body[..maxBody] : body,
            BodyTruncated: truncated,
            Attachments: attachments);
    }

    /// <summary>The message's readable body: the first text/plain part, else the first text/html one as text.</summary>
    private static string BodyText(List<JsonElement> parts)
    {
        bool IsBody(JsonElement p, string type) =>
            string.Equals(Str(p, "mimeType"), type, StringComparison.OrdinalIgnoreCase)
            && NullIfEmpty(Str(p, "filename")) is null
            && p.TryGetProperty("body", out var b) && Str(b, "data") is not null;

        if (parts.FirstOrDefault(p => IsBody(p, "text/plain")) is { ValueKind: JsonValueKind.Object } plain)
            return Decode(Base64Url(Str(plain.GetProperty("body"), "data")!), Header(plain, "Content-Type")).Trim();
        if (parts.FirstOrDefault(p => IsBody(p, "text/html")) is { ValueKind: JsonValueKind.Object } html)
            return HtmlToText(Decode(Base64Url(Str(html.GetProperty("body"), "data")!), Header(html, "Content-Type")));
        return "";
    }

    private static IEnumerable<JsonElement> Parts(JsonElement part)
    {
        yield return part;
        if (part.TryGetProperty("parts", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var p in Parts(child))
                    yield return p;
    }

    private DraftResult ToDraftResult(string account, JsonElement draft)
    {
        var message = draft.GetProperty("message");
        var messageId = Str(message, "id") ?? "";
        var email = api.Account(account).Email;
        return new DraftResult(
            DraftId: Str(draft, "id")!,
            MessageId: messageId,
            ThreadId: Str(message, "threadId"),
            Link: $"https://mail.google.com/mail/u/{Uri.EscapeDataString(email)}/#drafts?compose={messageId}",
            Note: "Saved as a draft in Gmail; it has not been sent. Tell the user it is waiting for them to review and send.");
    }

    private static IEnumerable<MailboxAddress> Parse(IEnumerable<string> values, string name)
    {
        foreach (var value in values)
        {
            if (!MailboxAddress.TryParse(value.Trim(), out var address) || !address.Address.Contains('@'))
                throw new McpException($"{name}: '{value}' is not a valid email address");
            yield return address;
        }
    }

    private static List<MailboxAddress> Addresses(string header) =>
        InternetAddressList.TryParse(header, out var list) ? list.Mailboxes.ToList() : [];

    private static string Format(MailboxAddress a) =>
        string.IsNullOrEmpty(a.Name) || a.Name == a.Address ? a.Address : $"{a.Name} <{a.Address}>";

    private static bool IsMe(MailboxAddress address, string me) => string.Equals(address.Address, me, StringComparison.OrdinalIgnoreCase);

    private static bool SameAddress(MailboxAddress a, MailboxAddress b) => string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase);

    private static void Replace(InternetAddressList list, IEnumerable<MailboxAddress> addresses)
    {
        list.Clear();
        list.AddRange(addresses);
    }

    private static string Raw(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static byte[] Base64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static IReadOnlyList<string> Labels(JsonElement m) =>
        m.TryGetProperty("labelIds", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

    private string? FormatInternalDate(JsonElement m) =>
        Str(m, "internalDate") is { } ms && long.TryParse(ms, out var value)
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(value), _zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
            : null;

    private static string? Header(JsonElement part, string name)
    {
        if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("headers", out var headers))
            return null;
        foreach (var h in headers.EnumerateArray())
            if (string.Equals(Str(h, "name"), name, StringComparison.OrdinalIgnoreCase))
                return Str(h, "value");
        return null;
    }

    internal static bool IsText(string? contentType, string? name)
    {
        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || TextTypes.Contains(type))
            return true;
        var generic = type.Length == 0 || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
        return generic && name is not null && TextExtensions.Contains(Path.GetExtension(name));
    }

    private static string Decode(byte[] bytes, string? contentType)
    {
        var charset = CharsetPattern().Match(contentType ?? "") is { Success: true } m ? m.Groups[1].Value : null;
        if (charset is not null && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Unknown charset; fall through to UTF-8.
            }
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string HtmlToText(string html)
    {
        var text = HiddenBlocks().Replace(html, "");
        text = BlockTags().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    [GeneratedRegex(@"^\s*(re|aw|sv|antw)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"charset\s*=\s*""?([\w.:-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetPattern();

    [GeneratedRegex(@"<(style|script|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLines();
}
