namespace RockBot.Host;

/// <summary>
/// The one rule for whether a pricing row's prefix prices a model id, shared by the cost estimator
/// and the routing analyzer so both cost a model the same way.
/// </summary>
/// <remarks>
/// A row matches when the model id contains its prefix, ignoring case, with <c>.</c> and <c>-</c>
/// treated as the same character. Providers spell one model's version both ways: the Anthropic API
/// calls it <c>claude-haiku-5-5</c> and OpenRouter <c>anthropic/claude-haiku-5.5</c>. Without this,
/// a fallback to OpenRouter matched no row and was costed at $0.
/// </remarks>
public static class LlmPricingMatch
{
    public static bool Matches(string modelId, string prefix)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(prefix);

        if (prefix.Length == 0)
            return true;

        // Search for the first character of the prefix, then compare the rest in place: no
        // allocation on the hot path, where every LLM response is costed.
        for (var start = 0; start + prefix.Length <= modelId.Length; start++)
        {
            var i = 0;
            while (i < prefix.Length && Same(modelId[start + i], prefix[i]))
                i++;
            if (i == prefix.Length)
                return true;
        }
        return false;
    }

    private static bool Same(char a, char b) =>
        char.ToLowerInvariant(Normalize(a)) == char.ToLowerInvariant(Normalize(b));

    private static char Normalize(char c) => c == '.' ? '-' : c;
}
