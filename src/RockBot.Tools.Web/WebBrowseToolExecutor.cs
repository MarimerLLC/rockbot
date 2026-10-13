using System.Text;
using System.Text.Json;
using RockBot.Host;

namespace RockBot.Tools.Web;

internal sealed class WebBrowseToolExecutor(
    IWebBrowseProvider browseProvider,
    IWorkingMemory? workingMemory,
    WebToolOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string url;
        bool render;

        try
        {
            var args = ParseArguments(request.Arguments);
            if (!args.TryGetValue("url", out var urlElement))
                return Error(request, "Missing required argument: url");

            url = urlElement.GetString() ?? string.Empty;
            render = args.TryGetValue("render", out var renderElement) && IsTrue(renderElement);
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        try
        {
            var page = await browseProvider.FetchAsync(url, new WebFetchOptions { ForceRender = render }, ct);
            var preamble = BuildPreamble(page);

            var fullContent = string.IsNullOrWhiteSpace(page.Title)
                ? page.Content
                : $"# {page.Title}\n\n{page.Content}";

            if (fullContent.Length <= options.ChunkingThreshold)
            {
                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = $"{preamble}\n\n{fullContent}",
                    IsError = false
                };
            }

            // Large content path
            if (workingMemory != null && request.SessionId != null)
            {
                return await ChunkIntoWorkingMemoryAsync(request, page, preamble, fullContent, url, ct);
            }

            // Fallback: no session or working memory — truncate for backward compatibility
            var truncated = fullContent[..options.ChunkingThreshold] +
                $"\n\n[Content truncated — {fullContent.Length - options.ChunkingThreshold:N0} chars omitted]";

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"{preamble}\n\n{truncated}",
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Failed to fetch page: {ex.Message}");
        }
    }

    private async Task<ToolInvokeResponse> ChunkIntoWorkingMemoryAsync(
        ToolInvokeRequest request,
        WebPageContent page,
        string preamble,
        string fullContent,
        string url,
        CancellationToken ct)
    {
        var chunks = ContentChunker.Chunk(fullContent, options.ChunkMaxLength);
        var ttl = TimeSpan.FromMinutes(options.ChunkTtlMinutes);
        var sanitizedUrl = SanitizeUrlForKey(url);
        var keyBase = $"{request.SessionId}/web-{sanitizedUrl}";

        var chunkKeys = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            // SessionId is the full working memory namespace (e.g. "session/abc123", "patrol/heartbeat")
            var key = $"{keyBase}-chunk{i}";
            chunkKeys.Add(key);
            await workingMemory!.SetAsync(key, chunks[i].Content, ttl, category: "web");
        }

        // Store hierarchical outline as index chunk
        var indexKey = $"{keyBase}-index";
        var outline = ContentChunker.BuildOutline(chunks, chunkKeys);
        await workingMemory!.SetAsync(indexKey, outline, ttl, category: "web");

        var index = new StringBuilder();
        index.AppendLine(preamble);
        index.AppendLine();
        index.AppendLine($"Page \"{page.Title}\" has been split into {chunks.Count} chunk(s) stored in working memory.");
        index.AppendLine($"A document outline is stored at key `{indexKey}` — retrieve it with " +
            "get_from_working_memory to navigate the content by section heading.");
        index.AppendLine("Call get_from_working_memory(key) for each relevant chunk BEFORE drawing any conclusions.");
        index.AppendLine("Do not summarise or answer based on this index alone — retrieve the chunks first.");
        index.AppendLine();
        index.AppendLine("| # | Heading | Key |");
        index.AppendLine("|---|---------|-----|");

        for (var i = 0; i < chunks.Count; i++)
        {
            var displayHeading = string.IsNullOrWhiteSpace(chunks[i].Heading) ? $"Section {i}" : chunks[i].Heading;
            index.AppendLine($"| {i} | {displayHeading} | `{chunkKeys[i]}` |");
        }

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = index.ToString().Trim(),
            IsError = false
        };
    }

    /// <summary>
    /// Where the content came from and, when the page was not actually read, a status line
    /// that says so in words the model cannot mistake for page content.
    /// </summary>
    internal static string BuildPreamble(WebPageContent page)
    {
        var via = page.FetchedVia switch
        {
            WebFetchMethod.Browser => page.ConsentDismissed
                ? "headless browser, consent banner dismissed"
                : "headless browser",
            WebFetchMethod.Api => "site API",
            _ => "http"
        };

        var sb = new StringBuilder($"Source: {page.SourceUrl} (via {via})");

        if (page.Status != WebFetchStatus.Ok)
        {
            var (code, what) = page.Status switch
            {
                WebFetchStatus.BlockedByConsentWall => ("blocked_by_consent_wall",
                    "The site answered with a cookie-consent wall; the page body was NOT read."),
                WebFetchStatus.JsRequired => ("js_required",
                    "The page builds its content with JavaScript; the page body was NOT read."),
                _ => ("empty", "The page returned almost no readable text; the page body was NOT read.")
            };
            sb.Append($"\n[web_browse status: {code}] {what}");
            if (!string.IsNullOrWhiteSpace(page.Note))
                sb.Append($" {page.Note}");
            sb.Append(" Do not present facts about this page as confirmed: tell the user you could not " +
                "read it, and label anything taken from search results as unconfirmed.");
        }
        else if (!string.IsNullOrWhiteSpace(page.Note))
        {
            sb.Append($"\nNote: {page.Note}");
        }

        return sb.ToString();
    }

    private static bool IsTrue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
        _ => false
    };

    private static string SanitizeUrlForKey(string url)
    {
        // Keep only alphanumerics, dots, and hyphens; collapse the rest to underscores
        var sb = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        // Trim leading protocol noise (https___) and cap length
        var key = sb.ToString().TrimStart('_');
        return key.Length > 80 ? key[..80] : key;
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new()
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = message,
            IsError = true
        };

    private static Dictionary<string, JsonElement> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }
}
