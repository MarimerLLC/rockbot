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
    /// Maximum character length of prose text sent to the embedding model. Text exceeding
    /// this limit is truncated before embedding. Default 30000 (~7500 tokens at ~4 chars/token)
    /// leaves headroom below the 8192-token context window of models like <c>nomic-embed-text</c>.
    /// </summary>
    public int MaxInputChars { get; set; } = 30_000;

    /// <summary>
    /// Stricter character cap applied when the input looks like a structured or dense
    /// payload — JSON object/array, base64 blob, hash listing, or markdown with heavy
    /// URL / citation / identifier content. Such content tokenizes ~2× denser than
    /// prose, so a 19k-char structured blob can push past 9k tokens — over the 8192
    /// window — while the prose cap would happily let it through. Default 12000 (~6000
    /// tokens at ~2 chars/token) leaves the same headroom for structured input.
    /// Selection is centralized in <c>EmbeddingTextPreparer</c>; no caller picks the
    /// cap directly.
    /// </summary>
    public int MaxStructuredInputChars { get; set; } = 12_000;

    /// <summary>
    /// Minimum cosine similarity threshold for vector search results. Candidates below
    /// this threshold are excluded from hybrid ranking, preventing loosely related content
    /// from diluting keyword-matched results. Default 0.5.
    /// </summary>
    public float MinSimilarityThreshold { get; set; } = 0.5f;

    /// <summary>
    /// Returns true when a usable embedding endpoint has been configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);
}
