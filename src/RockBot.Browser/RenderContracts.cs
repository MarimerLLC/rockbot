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
