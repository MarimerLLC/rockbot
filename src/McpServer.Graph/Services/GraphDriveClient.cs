using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using static McpServer.Graph.Services.GraphApi;

namespace McpServer.Graph.Services;

public sealed record DriveItemSummary(string Name, string Path, bool IsFolder, long Size, DateTimeOffset? Modified);

public sealed record DriveItemDetail(string Name, string Path, bool IsFolder, long Size, DateTimeOffset? Modified, string? MimeType, string? WebUrl);

/// <summary>
/// One drive file's content: <see cref="Text"/> for text files, <see cref="Content"/> (base64) for
/// binary files, which the agent's bridge saves to the shared volume.
/// </summary>
public sealed record DriveFileContent(
    string Name,
    long Size,
    string? Text = null,
    bool TextTruncated = false,
    string? Content = null);

/// <summary>
/// Graph REST calls for OneDrive/SharePoint drives configured under <c>Graph:Drives</c>. A tool's
/// <c>path</c> argument is always "/{alias}/{item relative path}" — a synthetic absolute path, never
/// a Graph id — so the MCP bridge's path-prefix argument guard can allow or deny it per alias
/// without this server needing to know anything about read/write/no-access policy itself.
/// </summary>
public sealed class GraphDriveClient(GraphApi api, IOptions<GraphMailOptions> options)
{
    private const string Feature = "files";
    private const string ItemFields = "id,name,size,file,folder,lastModifiedDateTime";
    private const string DetailFields = ItemFields + ",webUrl";

    // Resolved once per alias and cached for the process lifetime: a drive id doesn't change once a
    // site or OneDrive exists. CancellationToken.None is deliberate — this task is shared across every
    // caller for the alias, so one caller's cancellation must never poison the cache for the next.
    private readonly ConcurrentDictionary<string, Task<string>> _driveIds = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DriveItemSummary>> ListDriveItemsAsync(string path, int top, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        var query = Query([("$top", top.ToString()), ("$select", ItemFields)]);
        using var doc = await api.GetAsync($"{ActionRef(driveId, relativePath, "children")}{query}", Feature, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(i => ToSummary(i, alias, relativePath)).ToList();
    }

    public async Task<DriveItemDetail> GetDriveItemAsync(string path, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        var reference = relativePath.Length == 0 ? $"drives/{driveId}/root" : ItemRef(driveId, relativePath);
        using var doc = await api.GetAsync($"{reference}{Query([("$select", DetailFields)])}", Feature, ct);
        var item = doc.RootElement;
        return new DriveItemDetail(
            Name: Str(item, "name") ?? "/",
            Path: path,
            IsFolder: item.TryGetProperty("folder", out _),
            Size: Size(item),
            Modified: Modified(item),
            MimeType: item.TryGetProperty("file", out var f) ? Str(f, "mimeType") : null,
            WebUrl: Str(item, "webUrl"));
    }

    public async Task<IReadOnlyList<DriveItemSummary>> SearchDriveItemsAsync(string path, string query, int top, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        // OData string-literal syntax: an embedded ' doubles to '', and the whole literal is then
        // percent-encoded as a single path segment (q isn't a ?query= parameter here).
        var literal = query.Trim().Replace("'", "''");
        var action = $"search(q='{Uri.EscapeDataString(literal)}')";
        var select = Query([("$top", top.ToString()), ("$select", ItemFields)]);
        using var doc = await api.GetAsync($"{ActionRef(driveId, relativePath, action)}{select}", Feature, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(i => ToSummary(i, alias, relativePath)).ToList();
    }

    public async Task<DriveFileContent> DownloadDriveFileAsync(string path, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        if (relativePath.Length == 0)
            throw new McpException("path must name a file, not a drive root");
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        var reference = ItemRef(driveId, relativePath);

        JsonElement meta;
        using (var doc = await api.GetAsync($"{reference}{Query([("$select", "name,size,file,folder")])}", Feature, ct))
            meta = doc.RootElement.Clone();
        if (meta.TryGetProperty("folder", out _))
            throw new McpException($"'{path}' is a folder, not a file");

        var name = Str(meta, "name") ?? relativePath;
        var mimeType = meta.TryGetProperty("file", out var f) ? Str(f, "mimeType") : null;
        var size = Size(meta);
        var max = options.Value.MaxDriveFileBytes;
        if (size > max)
            throw new McpException($"'{name}' is {size / 1_048_576.0:F1} MB, over the {max / 1_048_576} MB file limit");

        var bytes = await api.GetBytesAsync($"{reference}:/content", Feature, max, ct);
        if (!GraphMailClient.IsText(mimeType, name))
            return new DriveFileContent(name, bytes.LongLength, Content: Convert.ToBase64String(bytes));

        var text = DecodeUtf8(bytes);
        var limit = options.Value.MaxBodyChars;
        return text.Length > limit
            ? new DriveFileContent(name, bytes.LongLength, Text: text[..limit], TextTruncated: true)
            : new DriveFileContent(name, bytes.LongLength, Text: text);
    }

    public async Task<DriveItemSummary> UploadDriveFileAsync(string path, string contentBase64, bool overwrite, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        if (relativePath.Length == 0)
            throw new McpException("path must name a file, not a drive root");
        var entry = FindAlias(alias);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new McpException("content must be valid base64");
        }

        var driveId = await ResolveDriveIdAsync(entry).ConfigureAwait(false);
        var max = options.Value.MaxDriveFileBytes;
        if (bytes.LongLength > max)
            throw new McpException($"content is {bytes.LongLength / 1_048_576.0:F1} MB, over the {max / 1_048_576} MB file limit");

        var conflict = overwrite ? "replace" : "fail";
        var reference = $"{ItemRef(driveId, relativePath)}:/content{Query([("@microsoft.graph.conflictBehavior", conflict)])}";
        using var doc = await api.PutBytesAsync(reference, bytes, "application/octet-stream", Feature, ct)
            ?? throw new McpException("Microsoft Graph did not return the uploaded file.");
        return ToSummary(doc.RootElement, alias, ParentOf(relativePath));
    }

    public async Task<DriveItemSummary> CreateDriveFolderAsync(string path, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        if (relativePath.Length == 0)
            throw new McpException("path must name a folder under the drive root, not the root itself");
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        var parent = ParentOf(relativePath);
        var name = relativePath[(parent.Length == 0 ? 0 : parent.Length + 1)..];

        var body = new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "fail"
        };
        using var doc = await api.SendAsync(HttpMethod.Post, ActionRef(driveId, parent, "children"), body, Feature, ct)
            ?? throw new McpException("Microsoft Graph did not return the created folder.");
        return ToSummary(doc.RootElement, alias, parent);
    }

    public async Task DeleteDriveItemAsync(string path, CancellationToken ct)
    {
        var (alias, relativePath) = SplitPath(path);
        if (relativePath.Length == 0)
            throw new McpException("path must name an item, not the drive root");
        var driveId = await ResolveDriveIdAsync(FindAlias(alias)).ConfigureAwait(false);
        (await api.SendAsync(HttpMethod.Delete, ItemRef(driveId, relativePath), null, Feature, ct))?.Dispose();
    }

    // ---- aliases and paths -----------------------------------------------------------------

    private GraphDriveEntry FindAlias(string alias)
    {
        var entry = options.Value.Drives.FirstOrDefault(d => string.Equals(d.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
            return entry;
        var known = options.Value.Drives.Count == 0 ? "none configured" : string.Join(", ", options.Value.Drives.Select(d => d.Alias));
        throw new McpException($"unknown drive alias '{alias}'; configured aliases: {known}");
    }

    private Task<string> ResolveDriveIdAsync(GraphDriveEntry entry) =>
        _driveIds.GetOrAdd(entry.Alias, _ => ResolveDriveIdCoreAsync(entry));

    private async Task<string> ResolveDriveIdCoreAsync(GraphDriveEntry entry)
    {
        var relative = entry.UserPrincipalName is not null
            ? $"users/{Uri.EscapeDataString(entry.UserPrincipalName)}/drive"
            : $"sites/{Uri.EscapeDataString(entry.SiteId!)}/drive";
        using var doc = await api.GetAsync($"{relative}{Query([("$select", "id")])}", Feature, CancellationToken.None);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Splits "/{alias}/{a/b/c}" into (alias, "a/b/c"); "/{alias}" alone means the drive root.</summary>
    internal static (string Alias, string RelativePath) SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
            throw new McpException("path must start with '/', e.g. '/finance-ro/Budgets/2026.xlsx' or '/finance-ro' for the drive root");

        var segments = path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new McpException("path must name a drive alias, e.g. '/finance-ro'");
        foreach (var segment in segments.Skip(1))
        {
            if (segment is "." or ".." || segment.Length == 0)
                throw new McpException("path must not contain empty, '.' or '..' segments");
        }
        return (segments[0], string.Join("/", segments.Skip(1)));
    }

    private static string ParentOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? "" : relativePath[..slash];
    }

    /// <summary>A Graph path-addressed item reference, e.g. "drives/{id}/root:/Documents/file.txt".</summary>
    private static string ItemRef(string driveId, string relativePath) =>
        relativePath.Length == 0
            ? $"drives/{driveId}/root"
            : $"drives/{driveId}/root:/{string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>An action on an item, e.g. "drives/{id}/root:/Documents:/children" or "drives/{id}/root/children" at the drive root.</summary>
    private static string ActionRef(string driveId, string relativePath, string action) =>
        relativePath.Length == 0 ? $"drives/{driveId}/root/{action}" : $"{ItemRef(driveId, relativePath)}:/{action}";

    private static DriveItemSummary ToSummary(JsonElement item, string alias, string parentRelativePath)
    {
        var name = item.GetProperty("name").GetString()!;
        var childPath = parentRelativePath.Length == 0 ? name : $"{parentRelativePath}/{name}";
        return new DriveItemSummary(name, $"/{alias}/{childPath}", item.TryGetProperty("folder", out _), Size(item), Modified(item));
    }

    private static long Size(JsonElement item) => item.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;

    private static DateTimeOffset? Modified(JsonElement item) =>
        item.TryGetProperty("lastModifiedDateTime", out var m) && m.ValueKind == JsonValueKind.String ? m.GetDateTimeOffset() : null;

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
