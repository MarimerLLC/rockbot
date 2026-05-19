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
    /// Cheap heuristic for "should this input get the stricter structured cap." Two signals:
    /// <list type="number">
    /// <item>First non-whitespace char is <c>{</c> or <c>[</c> — covers JSON objects, arrays, JSON-Lines.</item>
    /// <item>Density check on a leading sample — BPE tokenizers split on whitespace and merge
    /// common subwords, so content with long whitespace-separated runs (URLs, hashes, identifiers,
    /// base64, dense markdown evidence slices with citations) tokenizes ~2× denser than English
    /// prose (~5-char average word length) and busts the embedding context window even when the
    /// char count is under the prose cap.</item>
    /// </list>
    /// </summary>
    private static bool IsStructured(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '{' || c == '[') return true;
            break;
        }

        // Density check only matters for inputs large enough to risk overflow at the
        // prose cap. Below ~1k chars the cap can't be exceeded anyway.
        if (text.Length < 1024) return false;

        const int sampleSize = 4096;
        var sampleLen = Math.Min(text.Length, sampleSize);
        var whitespaceCount = 0;
        var nonLetterNonWhitespace = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) whitespaceCount++;
            else if (!char.IsLetter(c)) nonLetterNonWhitespace++;
        }

        // Average run length between whitespace boundaries. English prose runs ~5;
        // URL/hash/identifier-heavy content runs much longer.
        var avgRunLength = whitespaceCount > 0
            ? (double)(sampleLen - whitespaceCount) / whitespaceCount
            : double.MaxValue;

        // Symbol/digit density (excluding whitespace). Prose sits ~5%; dense markdown
        // with URLs, dates, hashes, and citations climbs past 25%.
        var nonLetterRatio = (double)nonLetterNonWhitespace / sampleLen;

        return avgRunLength > 15 || nonLetterRatio > 0.25;
    }
}
