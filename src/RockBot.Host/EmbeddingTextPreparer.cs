using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Single source of truth for "what string does the embedding model actually see".
/// Centralizes input truncation so prose and structured payloads (JSON, JSON-Lines)
/// get appropriately different char caps — JSON tokenizes ~2× denser than prose, so
/// the prose cap can blow past an 8k-token context window on otherwise-modest inputs.
/// All embedding call sites (long-term memory, skills, working memory) route through
/// this preparer instead of slicing locally.
/// </summary>
internal sealed class EmbeddingTextPreparer(
    IOptions<EmbeddingOptions> options,
    ILogger<EmbeddingTextPreparer> logger)
{
    private readonly EmbeddingOptions _opts = options.Value;

    /// <summary>
    /// Test-only convenience factory. Tests that exercise the embedding path don't care
    /// about preparer behaviour and don't want every fixture to construct an options
    /// shell — returns a preparer with default <see cref="EmbeddingOptions"/>.
    /// </summary>
    internal static EmbeddingTextPreparer ForTests() =>
        new(Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EmbeddingTextPreparer>.Instance);

    /// <summary>
    /// Returns the text the embedder should receive — truncated to the appropriate
    /// per-shape cap when needed, otherwise unchanged. <paramref name="diagnosticKey"/>
    /// is purely for log correlation; it does not affect the output.
    /// </summary>
    public string Prepare(string text, string? diagnosticKey = null)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var structured = IsStructured(text);
        var cap = structured ? _opts.MaxStructuredInputChars : _opts.MaxInputChars;
        if (text.Length <= cap)
            return text;

        logger.LogInformation(
            "Truncating embedding input{KeyTag} from {Original} to {Cap} chars (structured={Structured})",
            diagnosticKey is null ? "" : $" for '{diagnosticKey}'",
            text.Length, cap, structured);
        return text[..cap];
    }

    /// <summary>
    /// Cheap heuristic: input is treated as structured when the first non-whitespace
    /// character is <c>{</c> or <c>[</c>. Covers JSON objects, arrays, and JSON-Lines.
    /// Misses base64/HTML/code blobs, but those don't currently exceed the prose cap
    /// in practice — promote the heuristic only if real failures show up.
    /// </summary>
    private static bool IsStructured(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) continue;
            return c == '{' || c == '[';
        }
        return false;
    }
}
