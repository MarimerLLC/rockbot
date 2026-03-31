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
    private const double DefaultLowCeiling      = 0.25;
    private const double DefaultBalancedCeiling = 0.55;

    // ── Complexity signals → push toward High tier ───────────────────────────
    private static readonly string[] DefaultHighSignalKeywords =
    [
        "analyze", "analyse", "design", "architect", "evaluate", "critique",
        "trade-off", "tradeoff", "trade off", "compare and contrast", "compare",
        "prove", "derive", "demonstrate why", "reason through",
        "implement a system", "build a system", "step by step",
        "microservice", "distributed", "concurrent", "asynchronous", "async",
        "optimize", "performance bottleneck", "scalable", "scalability",
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
        "what is", "what's", "who is", "who was", "when was", "when is",
        "where is", "what time", "what day",
        "define", "definition of", "spell", "translate",
        "capital of", "how many", "list the", "give me a list",
        "yes or no", "true or false", "convert", "format",
        // Conversational / greeting patterns
        "hello", "hey", "thanks", "thank you", "good morning", "good afternoon",
        // Simple operational / tool-use patterns
        "check my", "send a", "send an", "remind me",
        "tell me about", "show me", "look up",
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
    public ModelTier SelectTier(string promptText) => Classify(promptText).Tier;

    /// <inheritdoc/>
    public TierClassification Classify(string promptText)
    {
        var config = GetEffectiveConfig();
        var lower = promptText.ToLowerInvariant();

        var matchedHigh = config.HighSignalKeywords
            .Where(k => ContainsWholePhrase(lower, k))
            .ToArray();
        var matchedLow = config.LowSignalKeywords
            .Where(k => ContainsWholePhrase(lower, k))
            .ToArray();

        var score = ComputeScore(promptText, config, matchedHigh.Length, matchedLow.Length);
        var tier = score <= config.LowCeiling      ? ModelTier.Low
                 : score <= config.BalancedCeiling ? ModelTier.Balanced
                 :                                   ModelTier.High;

        return new TierClassification(tier, score, matchedHigh, matchedLow);
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

            var highKeywords = SanitizeKeywords(dto.HighSignalKeywords, "highSignalKeywords");
            var lowKeywords  = SanitizeKeywords(dto.LowSignalKeywords, "lowSignalKeywords");

            var result = new EffectiveConfig(
                LowCeiling:          dto.LowCeiling      ?? DefaultLowCeiling,
                BalancedCeiling:     dto.BalancedCeiling  ?? DefaultBalancedCeiling,
                HighSignalKeywords:  highKeywords ?? DefaultHighSignalKeywords,
                LowSignalKeywords:   lowKeywords  ?? DefaultLowSignalKeywords);

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

    private static double ComputeScore(string prompt, EffectiveConfig config,
        int? complexSignalCount = null, int? simplexSignalCount = null)
    {
        var wordCount     = CountWords(prompt);
        var hasCode       = CodeBlockRegex.IsMatch(prompt);
        var hasMath       = MathRegex.IsMatch(prompt);
        var hasMultiStep  = MultiStepRegex.IsMatch(prompt);

        // Use pre-computed counts when available (avoids a second scan over the keyword lists)
        var lower = complexSignalCount is null || simplexSignalCount is null
            ? prompt.ToLowerInvariant()
            : string.Empty;

        var complexSignals = complexSignalCount
            ?? config.HighSignalKeywords.Count(k => ContainsWholePhrase(lower, k));
        var simplexSignals = simplexSignalCount
            ?? config.LowSignalKeywords.Count(k => ContainsWholePhrase(lower, k));

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
        var keywordScore = Math.Clamp(complexSignals * 0.10 - simplexSignals * 0.08, -0.15, 0.35);

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

    // ── Keyword validation ────────────────────────────────────────────────────

    private const int MinKeywordLength = 3;

    /// <summary>
    /// Topic/domain words that must never appear as high-signal complexity keywords.
    /// These indicate *what* a prompt is about, not *how hard* it is to reason about.
    /// The dream routing review LLM is instructed to avoid these, but this serves as
    /// a code-level guardrail in case the directive is ignored.
    /// </summary>
    private static readonly HashSet<string> TopicBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Communication / PIM
        "email", "emails", "inbox", "calendar", "calendar event", "calendar events",
        "schedule", "scheduled", "todo", "todo list", "task list", "flight",
        // Tools / infrastructure
        "mcp server", "mcp servers", "mcp service", "mcp services", "server",
        "working memory", "long term memory", "retrieve", "skill", "tool guide",
        // Health / personal
        "health report", "heart rhythm", "afib", "medical episode",
        // Generic actions
        "create", "remove", "mark complete", "mark as complete", "check",
        "paid bill", "bill payment",
    };

    /// <summary>
    /// Filters out keywords that are too short to be useful routing signals.
    /// For high-signal lists, also strips topic/domain words that indicate subject
    /// matter rather than cognitive complexity.
    /// Returns null when the input list is null (caller falls back to defaults).
    /// </summary>
    private string[]? SanitizeKeywords(List<string>? keywords, string listName)
    {
        if (keywords is null) return null;

        var isHighSignal = listName.Contains("high", StringComparison.OrdinalIgnoreCase);

        var filtered = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Trim().Length >= MinKeywordLength)
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        // For high-signal keywords, strip topic/domain words
        string[] afterBlocklist;
        if (isHighSignal)
        {
            afterBlocklist = filtered
                .Where(k => !TopicBlocklist.Contains(k))
                .ToArray();

            var blocked = filtered.Length - afterBlocklist.Length;
            if (blocked > 0)
            {
                var blockedWords = filtered.Where(k => TopicBlocklist.Contains(k));
                _logger?.LogWarning(
                    "KeywordTierSelector: stripped {Count} topic word(s) from {List} (domain indicators, not complexity signals): [{Keywords}]",
                    blocked, listName, string.Join(", ", blockedWords.Select(k => $"\"{k}\"")));
            }
        }
        else
        {
            afterBlocklist = filtered;
        }

        var removed = keywords.Count - afterBlocklist.Length;
        if (removed > 0)
        {
            var tooShort = keywords.Where(k => string.IsNullOrWhiteSpace(k) || k.Trim().Length < MinKeywordLength);
            if (tooShort.Any())
                _logger?.LogWarning(
                    "KeywordTierSelector: dropped {Count} keyword(s) from {List} (too short or blank): [{Keywords}]",
                    tooShort.Count(), listName, string.Join(", ", tooShort.Select(k => $"\"{k}\"")));
        }

        return afterBlocklist.Length > 0 ? afterBlocklist : null;
    }

    // ── Word-boundary matching ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="keyword"/> appears in <paramref name="text"/>
    /// with word boundaries on each side where the keyword itself starts/ends with a
    /// word character. This prevents "to" matching inside "tomorrow" or "try" inside
    /// "country", while still allowing multi-word phrases like "trade off" and
    /// intentional trailing-space keywords to work naturally.
    /// </summary>
    internal static bool ContainsWholePhrase(string text, string keyword)
    {
        if (keyword.Length == 0) return false;

        var checkStart = char.IsLetterOrDigit(keyword[0]);
        var checkEnd   = char.IsLetterOrDigit(keyword[^1]);
        var index = 0;

        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            var startOk = !checkStart
                          || index == 0
                          || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + keyword.Length;
            var endOk = !checkEnd
                        || end >= text.Length
                        || !char.IsLetterOrDigit(text[end]);

            if (startOk && endOk)
                return true;

            index++;
        }

        return false;
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed record EffectiveConfig(
        double LowCeiling,
        double BalancedCeiling,
        string[] HighSignalKeywords,
        string[] LowSignalKeywords);

    private sealed record CachedConfig(EffectiveConfig Config, DateTime LoadedAt);
}
