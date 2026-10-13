namespace RockBot.Tools.Web;

/// <summary>
/// Fetched web page content in Markdown format.
/// </summary>
public sealed record WebPageContent
{
    public required string Title { get; init; }

    /// <summary>
    /// Page body converted to Markdown.
    /// </summary>
    public required string Content { get; init; }

    public required string SourceUrl { get; init; }

    /// <summary>
    /// Whether <see cref="Content"/> is the page itself, or a shell that stood in for it
    /// (a consent wall, a JavaScript loader, an empty body). Anything other than
    /// <see cref="WebFetchStatus.Ok"/> means the page was not actually read.
    /// </summary>
    public WebFetchStatus Status { get; init; } = WebFetchStatus.Ok;

    /// <summary>How the content was obtained.</summary>
    public WebFetchMethod FetchedVia { get; init; } = WebFetchMethod.Http;

    /// <summary>True when the headless browser clicked away a cookie-consent banner.</summary>
    public bool ConsentDismissed { get; init; }

    /// <summary>
    /// One sentence on how the fetch went beyond the status, e.g. why rendering was not
    /// tried or how it failed. Shown to the model next to the content.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// Outcome of a page fetch, judged from what came back rather than from the HTTP status.
/// </summary>
public enum WebFetchStatus
{
    /// <summary>The page body was read.</summary>
    Ok,

    /// <summary>A cookie-consent wall stood in front of the content.</summary>
    BlockedByConsentWall,

    /// <summary>The page builds its content with JavaScript and the HTML held only a loader.</summary>
    JsRequired,

    /// <summary>The page returned almost no readable text, for no recognisable reason.</summary>
    Empty
}

/// <summary>How a page's content was obtained.</summary>
public enum WebFetchMethod
{
    /// <summary>A plain HTTP GET; no JavaScript ran.</summary>
    Http,

    /// <summary>Rendered in a headless browser by the render service.</summary>
    Browser,

    /// <summary>Read from a site API instead of the HTML page (e.g. GitHub issues).</summary>
    Api
}
