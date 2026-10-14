using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Reads the context limit and the size of the rejected prompt out of a provider's
/// context-overflow error, so the caller can trim history by the right amount and retry.
/// </summary>
internal static partial class ContextOverflowParser
{
    /// <summary>
    /// Recognises OpenAI's "maximum context length is 128000 tokens. However, your messages
    /// resulted in 130000 tokens" and Anthropic's "prompt is too long: 208310 tokens &gt; 200000
    /// maximum".
    /// </summary>
    public static bool TryParse(string message, out int maxTokens, out int usedTokens)
    {
        maxTokens = 0;
        usedTokens = 0;

        var maxMatch = OpenAiMax().Match(message);
        var usedMatch = OpenAiUsed().Match(message);
        if (maxMatch.Success && usedMatch.Success)
        {
            maxTokens = int.Parse(maxMatch.Groups[1].Value);
            usedTokens = int.Parse(usedMatch.Groups[1].Value);
            return true;
        }

        var anthropic = AnthropicTooLong().Match(message);
        if (anthropic.Success)
        {
            usedTokens = int.Parse(anthropic.Groups[1].Value);
            maxTokens = int.Parse(anthropic.Groups[2].Value);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"maximum context length is (\d+)")]
    private static partial Regex OpenAiMax();

    [GeneratedRegex(@"resulted in (\d+) tokens")]
    private static partial Regex OpenAiUsed();

    [GeneratedRegex(@"prompt is too long: (\d+) tokens > (\d+) maximum")]
    private static partial Regex AnthropicTooLong();
}
