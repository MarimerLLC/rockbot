using System.Text.RegularExpressions;
using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// Selects an <see cref="ModelTier"/> from prompt text using lightweight keyword
/// and structural heuristics — no embeddings, no external calls.
/// Ported from the LlmRouter spike at /home/rockylhotka/src/rdl/LlmRouter.
/// </summary>
public sealed class KeywordTierSelector : ILlmTierSelector
{
    private const double LowCeiling      = 0.28;
    private const double BalancedCeiling = 0.55;

    // ── Complexity signals → push toward High tier ───────────────────────────
    private static readonly string[] HighSignalKeywords =
    [
        "analyze", "analyse", "design", "architect", "evaluate", "critique",
        "trade-off", "tradeoff", "trade off", "compare and contrast",
        "prove", "derive", "demonstrate why", "reason through",
        "implement a system", "build a system", "step by step",
        "microservice", "distributed", "concurrent", "asynchronous",
        "optimize", "performance bottleneck", "scalab",
        "security implication", "threat model",
        "explain in depth", "comprehensive", "thorough analysis",
        "multiple approaches", "pros and cons", "disadvantage",
    ];

    // ── Simplicity signals → push toward Low tier ────────────────────────────
    private static readonly string[] LowSignalKeywords =
    [
        "what is", "what's", "who is", "who was", "when was", "where is",
        "define ", "definition of", "spell ", "translate ",
        "capital of", "how many", "list the", "give me a list",
        "yes or no", "true or false", "convert ", "format ",
    ];

    // ── Code / math / multi-step markers ────────────────────────────────────
    private static readonly Regex CodeBlockRegex = new(
        @"```|`[^`]+`|\bfunction\b|\bclass\b|\bdef\b|\bvoid\b|\bint\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MathRegex = new(
        @"\d+\s*[\+\-\*\/\^=]\s*\d+|∑|∫|√|≤|≥|∈|∀|∃|\bequation\b|\bformula\b|\bprove\b|\bderive\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiStepRegex = new(
        @"\b(first|then|next|finally|step \d|^\d+\.|additionally)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex SentenceRegex = new(@"[.!?]+", RegexOptions.Compiled);

    /// <inheritdoc/>
    public ModelTier SelectTier(string promptText)
    {
        var score = ComputeScore(promptText);
        return score <= LowCeiling      ? ModelTier.Low
             : score <= BalancedCeiling ? ModelTier.Balanced
             :                           ModelTier.High;
    }

    private static double ComputeScore(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        var wordCount     = CountWords(prompt);
        var hasCode       = CodeBlockRegex.IsMatch(prompt);
        var hasMath       = MathRegex.IsMatch(prompt);
        var hasMultiStep  = MultiStepRegex.IsMatch(prompt);

        var complexSignals = HighSignalKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
        var simplexSignals = LowSignalKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));

        // Length component (0 – 0.40): longer prompts tend to be more complex
        var lengthScore = wordCount switch
        {
            <= 10  => 0.05,
            <= 25  => 0.12,
            <= 50  => 0.20,
            <= 100 => 0.30,
            <= 200 => 0.38,
            _      => 0.40
        };

        // Keyword component (0 – 0.35)
        var keywordScore = Math.Clamp(complexSignals * 0.10 - simplexSignals * 0.08, 0.0, 0.35);

        // Structural indicators (0 – 0.25)
        var structureScore = 0.0;
        if (hasCode)      structureScore += 0.10;
        if (hasMath)      structureScore += 0.12;
        if (hasMultiStep) structureScore += 0.08;
        structureScore = Math.Min(0.25, structureScore);

        return Math.Clamp(lengthScore + keywordScore + structureScore, 0.0, 1.0);
    }

    private static int CountWords(string text) =>
        text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
}
