using System.Text.Json.Serialization;

namespace RockBot.Tools.Web.Brave;

internal sealed class BraveSearchResponse
{
    [JsonPropertyName("web")]
    public BraveWebResults? Web { get; set; }
}

internal sealed class BraveWebResults
{
    [JsonPropertyName("results")]
    public List<BraveResult>? Results { get; set; }
}

internal sealed class BraveResult
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
