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

        try
        {
            var args = ParseArguments(request.Arguments);
            if (!args.TryGetValue("url", out var urlElement))
                return Error(request, "Missing required argument: url");

            url = urlElement.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        try
        {
            var page = await browseProvider.FetchAsync(url, ct);

            var fullContent = string.IsNullOrWhiteSpace(page.Title)
                ? page.Content
                : $"# {page.Title}\n\n{page.Content}";

            if (fullContent.Length <= options.ChunkingThreshold)
            {
                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = fullContent,
                    IsError = false
                };
            }

            // Large content path
            if (workingMemory != null && request.SessionId != null)
            {
                return await ChunkIntoWorkingMemoryAsync(request, page, fullContent, url, ct);
            }

            // Fallback: no session or working memory — truncate for backward compatibility
            var truncated = fullContent[..options.ChunkingThreshold] +
                $"\n\n[Content truncated — {fullContent.Length - options.ChunkingThreshold:N0} chars omitted]";

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = truncated,
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
        index.AppendLine($"Page \"{page.Title}\" has been split into {chunks.Count} chunk(s) stored in working memory.");
        index.AppendLine($"A document outline is stored at key `{indexKey}` — retrieve it with " +
            "GetFromWorkingMemory to navigate the content by section heading.");
        index.AppendLine("Call GetFromWorkingMemory(key) for each relevant chunk BEFORE drawing any conclusions.");
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
