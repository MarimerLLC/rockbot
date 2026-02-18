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
}
