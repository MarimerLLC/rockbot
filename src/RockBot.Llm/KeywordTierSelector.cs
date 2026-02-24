using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// Selects an <see cref="ModelTier"/> from prompt text using lightweight keyword
/// and structural heuristics — no embeddings, no external calls.
/// Ported from the LlmRouter spike at /home/rockylhotka/src/rdl/LlmRouter.
///
/// <para>
/// When created via the parameterless constructor (tests), compiled defaults are always used.
/// When created via the DI constructor, keywords and thresholds are hot-reloaded every 60 s
/// from <c>{AgentBasePath}/tier-selector.json</c> (falls back to compiled defaults if missing).
/// </para>
/// </summary>
public sealed class KeywordTierSelector : ILlmTierSelector
{
    // ── Compiled defaults ─────────────────────────────────────────────────────
    private const double DefaultLowCeiling      = 0.15;
    private const double DefaultBalancedCeiling = 0.46;

    // ── Complexity signals → push toward High tier ───────────────────────────
    private static readonly string[] DefaultHighSignalKeywords =
    [
        "analyze", "analyse", "design", "architect", "evaluate", "critique",
        "trade-off", "tradeoff", "trade off", "compare and contrast", "compare",
        "prove", "derive", "demonstrate why", "reason through",
        "implement a system", "build a system", "step by step",
        "microservice", "distributed", "concurrent", "asynchronous", "async",
        "optimize", "performance bottleneck", "scalab",
        "security implication", "threat model",
        "explain in depth", "comprehensive", "thorough analysis",
        "multiple approaches", "pros and cons", "disadvantage",
        // Research / synthesis vocabulary — common in subagent task descriptions
        "research", "synthesize", "synthesise", "enterprise",
        "authentication", "authorization", "investigate",
        "technical brief", "technical analysis", "technical review",
    ];

    // ── Simplicity signals → push toward Low tier ────────────────────────────
    private static readonly string[] DefaultLowSignalKeywords =
    [
        "what is", "what's", "who is", "who was", "when was", "where is",
        "define ", "definition of", "spell ", "translate ",
        "capital of", "how many", "list the", "give me a list",
        "yes or no", "true or false", "convert ", "format ",
    ];

    private static readonly EffectiveConfig Defaults = new(
        DefaultLowCeiling, DefaultBalancedCeiling,
        DefaultHighSignalKeywords, DefaultLowSignalKeywords);

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // ── Hot-reload state (null when using parameterless ctor) ─────────────────
    private readonly string? _configPath;
    private readonly ILogger<KeywordTierSelector>? _logger;
    private volatile CachedConfig? _cache;
    private readonly object _cacheLock = new();

    // ── Parameterless constructor — used by tests, always uses compiled defaults ──
    public KeywordTierSelector() { }

    // ── DI constructor — resolves config path from AgentProfileOptions ────────
    // .NET DI picks the most-satisfied constructor automatically.
    public KeywordTierSelector(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<KeywordTierSelector> logger)
    {
        var basePath = profileOptions.Value.BasePath;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);

        _configPath = Path.Combine(basePath, "tier-selector.json");
        _logger = logger;
    }

    /// <inheritdoc/>
    public ModelTier SelectTier(string promptText)
    {
        var config = GetEffectiveConfig();
        var score = ComputeScore(promptText, config);
        return score <= config.LowCeiling      ? ModelTier.Low
             : score <= config.BalancedCeiling ? ModelTier.Balanced
             :                                   ModelTier.High;
    }

    // ── Hot-reload cache ──────────────────────────────────────────────────────

    private EffectiveConfig GetEffectiveConfig()
    {
        if (_configPath is null)
            return Defaults;

        // Volatile read: fast unsynchronised path when cache is warm
        var cached = _cache;
        if (cached is not null && DateTime.UtcNow - cached.LoadedAt < CacheTtl)
            return cached.Config;

        lock (_cacheLock)
        {
            // Double-checked: another thread may have refreshed while we waited
            cached = _cache;
            if (cached is not null && DateTime.UtcNow - cached.LoadedAt < CacheTtl)
                return cached.Config;

            var config = TryLoad();
            _cache = new CachedConfig(config, DateTime.UtcNow);
            return config;
        }
    }

    private EffectiveConfig TryLoad()
    {
        if (!File.Exists(_configPath!))
            return Defaults;

        try
        {
            var json = File.ReadAllText(_configPath!);
            var dto = JsonSerializer.Deserialize<TierSelectorConfig>(json, JsonOptions);
            if (dto is null)
                return Defaults;

            var result = new EffectiveConfig(
                LowCeiling:          dto.LowCeiling      ?? DefaultLowCeiling,
                BalancedCeiling:     dto.BalancedCeiling  ?? DefaultBalancedCeiling,
                HighSignalKeywords:  dto.HighSignalKeywords?.ToArray() ?? DefaultHighSignalKeywords,
                LowSignalKeywords:   dto.LowSignalKeywords?.ToArray()  ?? DefaultLowSignalKeywords);

            _logger?.LogInformation(
                "KeywordTierSelector: reloaded config from {Path} " +
                "(lowCeiling={Low}, balancedCeiling={Balanced}, " +
                "highSignals={HighCount}, lowSignals={LowCount})",
                _configPath, result.LowCeiling, result.BalancedCeiling,
                result.HighSignalKeywords.Length, result.LowSignalKeywords.Length);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "KeywordTierSelector: failed to load config from {Path}; using compiled defaults",
                _configPath);
            return Defaults;
        }
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static double ComputeScore(string prompt, EffectiveConfig config)
    {
        var lower = prompt.ToLowerInvariant();

        var wordCount     = CountWords(prompt);
        var hasCode       = CodeBlockRegex.IsMatch(prompt);
        var hasMath       = MathRegex.IsMatch(prompt);
        var hasMultiStep  = MultiStepRegex.IsMatch(prompt);

        var complexSignals = config.HighSignalKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
        var simplexSignals = config.LowSignalKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));

        // Length component (0 – 0.40): longer prompts tend to be more complex.
        // Fine-grained buckets in the 10-30 word range so concise-but-complex task
        // descriptions (subagent tasks, short research briefs) are distinguished from
        // genuinely simple short prompts.
        var lengthScore = wordCount switch
        {
            <= 10  => 0.05,
            <= 15  => 0.10,
            <= 20  => 0.15,
            <= 30  => 0.20,
            <= 50  => 0.28,
            <= 100 => 0.35,
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

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed record EffectiveConfig(
        double LowCeiling,
        double BalancedCeiling,
        string[] HighSignalKeywords,
        string[] LowSignalKeywords);

    private sealed record CachedConfig(EffectiveConfig Config, DateTime LoadedAt);
}
