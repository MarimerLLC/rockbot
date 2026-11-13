namespace RockBot.Browser;

/// <summary>Body of <c>POST /render</c>.</summary>
internal sealed record RenderRequest(string Url, int? TimeoutMs);

/// <summary>
/// Result of <c>POST /render</c>. <see cref="Error"/> is set, and the other content fields
/// are empty, when the page could not be rendered at all; a page that rendered but answered
/// 4xx/5xx reports that in <see cref="HttpStatus"/> instead.
/// </summary>
internal sealed record RenderResponse(
    string? FinalUrl,
    int HttpStatus,
    string? Title,
    string? Html,
    bool ConsentDismissed,
    string? Error)
{
    public static RenderResponse Failed(string error) => new(null, 0, null, null, false, error);
}

/// <summary>
/// Body of <c>POST /screenshot</c>: an HTML document to load and the viewport to capture.
/// </summary>
internal sealed record ScreenshotRequest(string? Html, int? Width, int? Height, int? TimeoutMs)
{
    /// <summary>Largest document accepted, in characters. A single-file deck is far smaller.</summary>
    public const int MaxHtmlChars = 5_000_000;

    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;
    public const int MinWidth = 320;
    public const int MaxWidth = 3840;
    public const int MinHeight = 240;
    public const int MaxHeight = 2160;

    /// <summary>Why <paramref name="request"/> cannot be served, or null when it can.</summary>
    public static string? Validate(ScreenshotRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Html))
            return "No HTML was supplied.";
        if (request.Html.Length > MaxHtmlChars)
            return $"The document is {request.Html.Length:N0} characters; the limit is {MaxHtmlChars:N0}.";
        return null;
    }

    /// <summary>The viewport to use: the requested size held within sane bounds, 1280x720 by default.</summary>
    public static (int Width, int Height) Viewport(ScreenshotRequest request) =>
        (Math.Clamp(request.Width ?? DefaultWidth, MinWidth, MaxWidth),
         Math.Clamp(request.Height ?? DefaultHeight, MinHeight, MaxHeight));
}

/// <summary>Error body of <c>POST /screenshot</c>; a success answers with the PNG itself.</summary>
internal sealed record ScreenshotError(string Error);

/// <summary>Outcome of one capture.</summary>
/// <param name="Png">The image, when the capture worked.</param>
/// <param name="BlockedRequests">Requests the document made that the guard refused.</param>
/// <param name="Error">What went wrong, when it did not.</param>
/// <param name="BadRequest">True when the fault is the request's rather than the page's.</param>
internal sealed record ScreenshotResult(byte[]? Png, int BlockedRequests, string? Error, bool BadRequest)
{
    public static ScreenshotResult Captured(byte[] png, int blockedRequests) => new(png, blockedRequests, null, false);
    public static ScreenshotResult Failed(string error) => new(null, 0, error, false);
    public static ScreenshotResult Rejected(string error) => new(null, 0, error, true);
}

/// <summary>Response headers of a successful <c>POST /screenshot</c>.</summary>
internal static class ScreenshotHeaders
{
    /// <summary>How many of the document's requests the guard refused. Missing assets explain a bare slide.</summary>
    public const string BlockedRequests = "X-RockBot-Blocked-Requests";
}
