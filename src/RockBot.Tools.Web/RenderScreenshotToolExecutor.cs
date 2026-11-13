using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace RockBot.Tools.Web;

/// <summary>
/// <c>render_screenshot</c>: loads an HTML file from the shared volume in the headless browser of
/// the <c>RockBot.Browser</c> render service and saves a PNG of the first viewport back to the
/// shared volume, so the agent can look at a deck or page it wrote (with <c>analyze_file</c>)
/// before showing it to the user (#669).
/// <para>
/// The file's text is posted to the service, which has no access to the shared volume; the
/// document must therefore be self-contained, with anything else it needs on a public URL.
/// </para>
/// </summary>
internal sealed class RenderScreenshotToolExecutor(
    IHttpClientFactory httpClientFactory,
    WebToolOptions options,
    TimeProvider? timeProvider = null) : IToolExecutor
{
    internal const string ToolName = "render_screenshot";

    /// <summary>Largest HTML file sent to the render service, in bytes.</summary>
    internal const long MaxHtmlBytes = 4 * 1024 * 1024;

    /// <summary>Header in which the service reports requests it refused.</summary>
    internal const string BlockedRequestsHeader = "X-RockBot-Blocked-Requests";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string path;
        string? outputPath;
        int? width, height;
        try
        {
            var args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];

            path = GetString(args, "path") ?? string.Empty;
            outputPath = GetString(args, "output_path");
            width = GetInt(args, "width");
            height = GetInt(args, "height");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path))
            return Error(request, "Missing required argument: path");
        if (string.IsNullOrWhiteSpace(options.RenderServiceUrl))
            return Error(request, "No render service is configured, so screenshots are not available.");

        var source = ResolveUnderBase(path);
        if (source is null)
            return Error(request, $"Invalid path '{path}': must be within the shared volume.");
        if (!File.Exists(source))
            return Error(request, $"File not found: {path}");

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".html" or ".htm"))
            return Error(request,
                $"'{path}' is not an HTML file. render_screenshot takes a .html file; " +
                "to look at an image or PDF that already exists, use analyze_file on it directly.");

        var length = new FileInfo(source).Length;
        if (length > MaxHtmlBytes)
            return Error(request,
                $"'{path}' is {length:N0} bytes; the limit for a screenshot is {MaxHtmlBytes:N0}. " +
                "Link large media instead of embedding it.");

        var stem = Path.GetFileNameWithoutExtension(source);
        var relativeOutput = string.IsNullOrWhiteSpace(outputPath)
            ? $"tmp/screenshots/{stem}-{_time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.png"
            : outputPath.Trim();
        if (!relativeOutput.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Error(request, $"Invalid output_path '{relativeOutput}': the screenshot is a PNG, so the name must end in .png.");

        var target = ResolveUnderBase(relativeOutput);
        if (target is null)
            return Error(request, $"Invalid output_path '{relativeOutput}': must be within the shared volume.");

        try
        {
            var html = await File.ReadAllTextAsync(source, ct);

            using var client = httpClientFactory.CreateClient(RenderServiceWebBrowseProvider.HttpClientName);
            var endpoint = $"{options.RenderServiceUrl!.TrimEnd('/')}/screenshot";
            using var response = await client.PostAsJsonAsync(
                endpoint,
                new ScreenshotRequest(html, width, height, options.RenderTimeoutSeconds * 1000),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadErrorAsync(response, ct);
                return Error(request, $"The screenshot failed: {reason}");
            }

            var png = await response.Content.ReadAsByteArrayAsync(ct);
            if (png.Length == 0)
                return Error(request, "The screenshot failed: the render service returned an empty image.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, png, ct);

            var shown = Path.GetRelativePath(Path.GetFullPath(options.SharedBasePath), target).Replace('\\', '/');
            var content =
                $"Saved a screenshot of {path} to {shown} ({png.Length:N0} bytes, first viewport only). " +
                $"You have not seen it yet: call analyze_file(path: \"{shown}\", prompt: ...) to check " +
                "layout, overflow and legibility before you tell the user it is ready.";

            if (response.Headers.TryGetValues(BlockedRequestsHeader, out var values) &&
                int.TryParse(values.FirstOrDefault(), out var blocked) && blocked > 0)
            {
                content +=
                    $" Note: {blocked} request(s) the page made were refused (private or local addresses, " +
                    "or a non-web scheme). Anything it loads must be on a public URL or inline in the file.";
            }

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = content,
                IsError = false
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            return Error(request, $"The screenshot failed: {ex.Message}");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ScreenshotError>(ct);
            if (!string.IsNullOrWhiteSpace(body?.Error))
                return body.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
        }
        return $"the render service answered HTTP {(int)response.StatusCode}.";
    }

    /// <summary>
    /// Resolves a shared-volume path, relative or absolute, and returns null when it escapes the
    /// volume. The comparison includes the separator, so a sibling directory whose name merely
    /// starts with the base's does not pass.
    /// </summary>
    private string? ResolveUnderBase(string path)
    {
        try
        {
            var fullBase = Path.GetFullPath(options.SharedBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(fullBase, path.Trim()));
            return full.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? GetString(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Models send numbers as numbers or as strings; take either.
    private static int? GetInt(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new()
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = message,
            IsError = true
        };

    internal sealed record ScreenshotRequest(string Html, int? Width, int? Height, int TimeoutMs);

    internal sealed record ScreenshotError(string? Error);
}
