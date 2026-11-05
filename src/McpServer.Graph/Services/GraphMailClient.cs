using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using static McpServer.Graph.Services.GraphApi;

namespace McpServer.Graph.Services;

public sealed record MessageSummary(
    string Id,
    string? Subject,
    string? From,
    DateTimeOffset? Received,
    bool IsRead,
    bool HasAttachments,
    string? Preview,
    string FlagStatus);

public sealed record MessageDetail(
    string Id,
    string? Subject,
    string? From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Received,
    bool IsRead,
    bool HasAttachments,
    string Body,
    bool BodyTruncated,
    IReadOnlyList<AttachmentInfo> Attachments,
    string FlagStatus);

/// <summary>An attachment's metadata. Kind is file, item (an attached email or event) or reference (a cloud link).</summary>
public sealed record AttachmentInfo(string Id, string? Name, string Kind, string? ContentType, long Size, bool IsInline);

/// <summary>
/// One attachment's content: <see cref="Text"/> for text files, <see cref="Content"/> (base64) for
/// binary files, which the agent's bridge saves to the shared volume, or <see cref="Message"/> for an
/// attached email.
/// </summary>
public sealed record AttachmentContent(
    string Id,
    string? Name,
    string Kind,
    string? ContentType,
    long Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool TextTruncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EmbeddedItem? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

/// <summary>The readable parts of an item attachment, usually an attached email.</summary>
public sealed record EmbeddedItem(
    string? Type,
    string? Subject,
    string? From,
    IReadOnlyList<string> To,
    DateTimeOffset? Received,
    string Body,
    bool BodyTruncated);

/// <summary>Read-only Graph REST calls, always against the single configured mailbox.</summary>
public sealed partial class GraphMailClient(GraphApi api, IOptions<GraphMailOptions> options)
{
    private const string AttachmentFields = "id,name,contentType,size,isInline";

    // Types returned as text rather than saved as files. Anything else goes to the bridge as base64.
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/csv", "application/yaml", "application/x-yaml",
        "message/rfc822", "image/svg+xml"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".ics", ".vcf", ".yaml", ".yml", ".eml", ".html", ".htm", ".svg"
    };

    private const string SummaryFields = "id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview,flag";
    private const string DetailFields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,isRead,hasAttachments,body,flag";

    public async Task<IReadOnlyList<MessageSummary>> ListMessagesAsync(string folder, int top, bool unreadOnly, bool flaggedOnly, CancellationToken ct)
    {
        var query = new List<(string, string)>
        {
            ("$top", top.ToString()),
            ("$select", SummaryFields),
            ("$orderby", "receivedDateTime desc")
        };
        // Graph rejects $orderby on a property absent from $filter, hence the always-true date clause.
        if (unreadOnly || flaggedOnly)
        {
            var clauses = new List<string> { "receivedDateTime ge 1900-01-01T00:00:00Z" };
            if (unreadOnly) clauses.Add("isRead eq false");
            if (flaggedOnly) clauses.Add("flag/flagStatus eq 'flagged'");
            query.Add(("$filter", string.Join(" and ", clauses)));
        }

        using var doc = await GetAsync($"{api.MailboxPath}/mailFolders/{Uri.EscapeDataString(folder)}/messages{Query(query)}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<MessageSummary> SetFlagAsync(string id, bool flagged, CancellationToken ct)
    {
        var patch = new { flag = new { flagStatus = flagged ? "flagged" : "notFlagged" } };
        using var doc = await api.SendAsync(HttpMethod.Patch, $"{api.MailboxPath}/messages/{Uri.EscapeDataString(id)}", patch, "mail", ct)
            ?? throw new McpException("Microsoft Graph did not return the updated message.");
        return ToSummary(doc.RootElement);
    }

    public async Task<IReadOnlyList<MessageSummary>> SearchMessagesAsync(string search, int top, CancellationToken ct)
    {
        var kql = "\"" + search.Replace("\"", string.Empty) + "\"";
        var query = Query([("$search", kql), ("$top", top.ToString()), ("$select", SummaryFields)]);

        using var doc = await GetAsync($"{api.MailboxPath}/messages{query}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<MessageDetail> GetMessageAsync(string id, CancellationToken ct)
    {
        using var doc = await GetAsync($"{api.MailboxPath}/messages/{Uri.EscapeDataString(id)}{Query([("$select", DetailFields)])}", textBody: true, ct);
        var m = doc.RootElement;

        var body = m.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var max = options.Value.MaxBodyChars;
        var truncated = body.Length > max;
        var attachments = Bool(m, "hasAttachments") ? await ListAttachmentsAsync(id, ct) : [];

        return new MessageDetail(
            Id: m.GetProperty("id").GetString()!,
            Subject: Str(m, "subject"),
            From: m.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
            To: Recipients(m, "toRecipients"),
            Cc: Recipients(m, "ccRecipients"),
            Received: m.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTimeOffset() : null,
            IsRead: Bool(m, "isRead"),
            HasAttachments: Bool(m, "hasAttachments"),
            Body: truncated ? body[..max] : body,
            BodyTruncated: truncated,
            Attachments: attachments,
            FlagStatus: FlagStatusOf(m));
    }

    public async Task<IReadOnlyList<AttachmentInfo>> ListAttachmentsAsync(string messageId, CancellationToken ct)
    {
        using var doc = await GetAsync($"{AttachmentsPath(messageId)}{Query([("$select", AttachmentFields)])}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToAttachmentInfo).ToList();
    }

    public async Task<AttachmentContent> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        var path = $"{AttachmentsPath(messageId)}/{Uri.EscapeDataString(attachmentId)}";
        AttachmentInfo info;
        using (var doc = await GetAsync($"{path}{Query([("$select", AttachmentFields)])}", textBody: false, ct))
            info = ToAttachmentInfo(doc.RootElement);

        var result = new AttachmentContent(info.Id, info.Name, info.Kind, info.ContentType, info.Size);
        switch (info.Kind)
        {
            case "reference":
                return result with { Note = "This attachment is a link to a file in OneDrive or SharePoint, not a copy; open it from the message in Outlook." };

            case "item":
                using (var doc = await GetAsync($"{path}{Query([("$expand", "microsoft.graph.itemattachment/item")])}", textBody: true, ct))
                {
                    return doc.RootElement.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                        ? result with { Message = ToEmbeddedItem(item) }
                        : result with { Note = "Graph returned no content for this attached item." };
                }

            default:
                var max = options.Value.MaxAttachmentBytes;
                if (info.Size > max)
                    throw new McpException($"'{info.Name}' is {info.Size / 1_048_576.0:F1} MB, over the {max / 1_048_576} MB attachment limit");
                var bytes = await api.GetBytesAsync($"{path}/$value", "mail", max, ct);
                result = result with { Size = bytes.LongLength };
                if (!IsText(info.ContentType, info.Name))
                    return result with { Content = Convert.ToBase64String(bytes) };

                var text = Decode(bytes, info.ContentType);
                var limit = options.Value.MaxBodyChars;
                return text.Length > limit
                    ? result with { Text = text[..limit], TextTruncated = true }
                    : result with { Text = text };
        }
    }

    private string AttachmentsPath(string messageId) => $"{api.MailboxPath}/messages/{Uri.EscapeDataString(messageId)}/attachments";

    private static AttachmentInfo ToAttachmentInfo(JsonElement a) => new(
        Id: a.GetProperty("id").GetString()!,
        Name: Str(a, "name"),
        Kind: Str(a, "@odata.type") switch
        {
            "#microsoft.graph.itemAttachment" => "item",
            "#microsoft.graph.referenceAttachment" => "reference",
            _ => "file"
        },
        ContentType: Str(a, "contentType"),
        Size: a.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0,
        IsInline: Bool(a, "isInline"));

    private EmbeddedItem ToEmbeddedItem(JsonElement item)
    {
        var body = "";
        if (item.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var c))
        {
            body = c.GetString() ?? "";
            if (string.Equals(Str(b, "contentType"), "html", StringComparison.OrdinalIgnoreCase))
                body = HtmlToText(body);
        }
        var max = options.Value.MaxBodyChars;
        var truncated = body.Length > max;
        var type = Str(item, "@odata.type")?.Replace("#microsoft.graph.", "");

        return new EmbeddedItem(
            Type: type,
            Subject: Str(item, "subject"),
            From: item.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
            To: Recipients(item, "toRecipients"),
            Received: item.TryGetProperty("receivedDateTime", out var r) && r.ValueKind == JsonValueKind.String ? r.GetDateTimeOffset() : null,
            Body: truncated ? body[..max] : body,
            BodyTruncated: truncated);
    }

    internal static bool IsText(string? contentType, string? name)
    {
        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || TextTypes.Contains(type))
            return true;
        // Outlook often labels attachments application/octet-stream; trust a text extension then.
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
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string HtmlToText(string html)
    {
        var text = BlockTags().Replace(html, "\n");
        text = Tags().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"charset\s*=\s*""?([\w.:-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetPattern();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLines();

    private static MessageSummary ToSummary(JsonElement m) => new(
        Id: m.GetProperty("id").GetString()!,
        Subject: Str(m, "subject"),
        From: m.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
        Received: m.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTimeOffset() : null,
        IsRead: Bool(m, "isRead"),
        HasAttachments: Bool(m, "hasAttachments"),
        Preview: Str(m, "bodyPreview"),
        FlagStatus: FlagStatusOf(m));

    private static string FlagStatusOf(JsonElement m) =>
        m.TryGetProperty("flag", out var flag) && flag.ValueKind == JsonValueKind.Object
            ? Str(flag, "flagStatus") ?? "notFlagged"
            : "notFlagged";

    private Task<JsonDocument> GetAsync(string relativeUri, bool textBody, CancellationToken ct) =>
        textBody
            ? api.GetAsync(relativeUri, "mail", ct, ("Prefer", "outlook.body-content-type=\"text\""))
            : api.GetAsync(relativeUri, "mail", ct);

    private static IReadOnlyList<string> Recipients(JsonElement m, string property) =>
        m.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(FormatAddress).OfType<string>().ToList()
            : [];
}
