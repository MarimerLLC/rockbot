namespace RockBot.Host;

/// <summary>
/// Configuration for the optional text-embedding model used by hybrid search.
/// When <see cref="Endpoint"/> is null or empty, vector search is disabled and
/// stores fall back to BM25-only ranking.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// Base URL of the embedding API (e.g. <c>http://ollama:11434</c> or an OpenAI-compatible endpoint).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Model name / deployment ID (e.g. <c>nomic-embed-text</c>, <c>text-embedding-3-small</c>).
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// API key. Optional — Ollama does not require one, but cloud providers do.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Maximum character length of text sent to the embedding model. Text exceeding this
    /// limit is truncated before embedding. Default 30000 (~7500 tokens) leaves headroom
    /// below the 8192-token context window of models like <c>nomic-embed-text</c>.
    /// </summary>
    public int MaxInputChars { get; set; } = 30_000;

    /// <summary>
    /// Returns true when a usable embedding endpoint has been configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);
}
