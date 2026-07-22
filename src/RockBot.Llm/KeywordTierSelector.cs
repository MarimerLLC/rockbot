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
    private const double DefaultLowCeiling           = 0.25;
    private const double DefaultBalancedCeiling      = 0.55;
    private const double DefaultTrivialGuardCeiling  = 0.15;
    private const double DefaultUserOriginBias       = 0.10;

    // ── Guardrails: dream-tuned values are clamped to these ranges ────────────
    private const double MinLowCeiling           = 0.15;
    private const double MaxLowCeiling           = 0.40;
    private const double MinBalancedCeiling      = 0.40;
    private const double MaxBalancedCeiling      = 0.80;
    private const double MinTrivialGuardCeiling  = 0.10;
    private const double MaxTrivialGuardCeiling  = 0.25;
    private const double MinUserOriginBias       = 0.0;
    private const double MaxUserOriginBias       = 0.20;

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
        "good night", "good evening", "how are you", "how's it going",
        // Casual conversational patterns — these dominate Balanced drift cases
        "i think", "i plan to", "what do you think", "i was thinking",
        "sounds good", "that's great", "got it", "okay",
        // Simple operational / tool-use patterns
        "check my", "send a", "send an", "remind me",
        "tell me about", "show me", "look up",
    ];

    // ── Balanced-floor signals → floor a Low-routed user query at Balanced ───
    // Empty by default (add-only caveat): the dream owns this list and no
    // un-retractable guesses are baked in. See issue #486.
    private static readonly string[] DefaultBalancedFloorKeywords = [];

    private static readonly EffectiveConfig Defaults = new(
        DefaultLowCeiling, DefaultBalancedCeiling,
        DefaultTrivialGuardCeiling, DefaultUserOriginBias,
        DefaultHighSignalKeywords, DefaultLowSignalKeywords,
        DefaultBalancedFloorKeywords);

    // ── Code / math / multi-step markers ────────────────────────────────────
    private static readonly Regex CodeBlockRegex = new(
        @"```|`[^`]+`|\bfunction\b|\bclass\b|\bdef\b|\bvoid\b|\bint\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Arithmetic detection deliberately treats '-' differently from the other
    // operators: a bare hyphen between digits is almost always a *range*, not
    // subtraction — clock times ("7:00-10:00"), ISO dates ("2025-01-01"), and
    // numeric ranges ("7-10") would otherwise score as "math" and inflate the
    // structural component of verbose-but-simple prompts. So '+', '*', '/', '^',
    // '=' match with optional surrounding space, but '-' only counts when it is
    // whitespace-flanked ("5 - 3"), which is how genuine subtraction is written.
    private static readonly Regex MathRegex = new(
        @"\d+\s*[\+\*\/\^=]\s*\d+|\d+\s+-\s+\d+|∑|∫|√|≤|≥|∈|∀|∃|\bequation\b|\bformula\b|\bprove\b|\bderive\b",
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
    public TierClassification Classify(string promptText) =>
        ClassifyCore(promptText, context: null);

    /// <inheritdoc/>
    public TierClassification Classify(string promptText, TierRoutingContext context) =>
        ClassifyCore(promptText, context);

    private TierClassification ClassifyCore(string promptText, TierRoutingContext? context)
    {
        var config = GetEffectiveConfig();
        var lower = promptText.ToLowerInvariant();

        var matchedHigh = config.HighSignalKeywords
            .Where(k => ContainsWholePhrase(lower, k))
            .ToArray();
        var matchedLow = config.LowSignalKeywords
            .Where(k => ContainsWholePhrase(lower, k))
            .ToArray();

        // Structural signals computed once here so the high-tier gate below can
        // reuse them without a second regex pass.
        var hasCode      = CodeBlockRegex.IsMatch(promptText);
        var hasMath      = MathRegex.IsMatch(promptText);
        var hasMultiStep = MultiStepRegex.IsMatch(promptText);

        var score = ComputeScore(promptText, matchedHigh.Length, matchedLow.Length,
            hasCode, hasMath, hasMultiStep);

        // Origin bias: user messages get a slight push toward lower tiers.
        // Subagent operational tasks stay neutral since they carry genuine complexity signals.
        // No lower clamp — stacks with negative keyword scores for stronger Low routing signal.
        if (context?.Origin == "user-message" && config.UserOriginBias > 0)
            score -= config.UserOriginBias;

        var tier = score <= config.LowCeiling      ? ModelTier.Low
                 : score <= config.BalancedCeiling ? ModelTier.Balanced
                 :                                   ModelTier.High;

        // High-tier gate: a prompt must show a genuine complexity signal — a
        // matched high-signal keyword, a code block, or real math — before it can
        // route High. Length and incidental multi-step phrasing ("then ...") alone
        // must not escalate past Balanced. This prevents long, well-specified but
        // cognitively simple operational task descriptions (e.g. PIM/tool-use
        // subagent briefs: "search email ... then check the calendar ... add the
        // missing events") from routing High purely on verbosity. The keyword list
        // is the only real complexity signal we have; absent any hit, Balanced is
        // the safe ceiling. See issue #471.
        if (tier == ModelTier.High
            && matchedHigh.Length == 0
            && !hasCode
            && !hasMath)
        {
            tier = ModelTier.Balanced;
        }

        // Trivial guard: force Low for objectively simple prompts regardless of
        // dream-tuned thresholds. This prevents threshold drift from absorbing
        // trivial user traffic into Balanced.
        var wordCount = CountWords(promptText);
        if (tier != ModelTier.Low
            && score < config.TrivialGuardCeiling
            && wordCount <= 20
            && matchedHigh.Length == 0)
        {
            tier = ModelTier.Low;
        }

        // Active-thread short-message override: when a short follow-up arrives on
        // an established conversational thread, route through Balanced. The
        // Low-tier model otherwise tends to summarise injected long-term memory
        // instead of continuing the thread — see issue #383. Bounded by the
        // matched-high-keyword gate so genuinely complex short prompts still
        // escalate naturally.
        if (context?.ThreadEstablished == true
            && tier == ModelTier.Low
            && promptText.Length <= ShortMessageHeuristics.UserMessageCharThreshold
            && matchedHigh.Length == 0)
        {
            tier = ModelTier.Balanced;
        }

        // Balanced-floor override: a first-turn user query naming a known tool/topic
        // (e.g. "what's on my todo list?") scores near-zero → Low, where the small model
        // fails to select the right MCP tool. When the dream has learned such a floor
        // keyword, escalate Low→Balanced (never High) so it lands on a cheap but
        // tool-capable tier. Exempt from TopicBlocklist by design. See issue #486.
        if (context?.Origin == "user-message"
            && tier == ModelTier.Low
            && config.BalancedFloorKeywords.Length > 0
            && config.BalancedFloorKeywords.Any(k => ContainsWholePhrase(lower, k)))
        {
            tier = ModelTier.Balanced;
        }

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

            // Merge dream keywords with compiled defaults (dream adds, never replaces).
            var highKeywords = MergeKeywords(DefaultHighSignalKeywords, dto.HighSignalKeywords, "highSignalKeywords");
            var lowKeywords  = MergeKeywords(DefaultLowSignalKeywords, dto.LowSignalKeywords, "lowSignalKeywords");
            // Balanced-floor list is exempt from the TopicBlocklist: SanitizeKeywords only
            // applies the blocklist when the list name contains "high", so "balancedFloorKeywords"
            // passes topic/tool words (todo, calendar, ...) through unfiltered — by design.
            var floorKeywords = MergeKeywords(DefaultBalancedFloorKeywords, dto.BalancedFloorKeywords, "balancedFloorKeywords");

            // Clamp dream-tuned thresholds to guardrail ranges.
            var lowCeiling = ClampThreshold(dto.LowCeiling, DefaultLowCeiling, MinLowCeiling, MaxLowCeiling, "lowCeiling");
            var balancedCeiling = ClampThreshold(dto.BalancedCeiling, DefaultBalancedCeiling, MinBalancedCeiling, MaxBalancedCeiling, "balancedCeiling");
            var trivialGuard = ClampThreshold(dto.TrivialGuardCeiling, DefaultTrivialGuardCeiling, MinTrivialGuardCeiling, MaxTrivialGuardCeiling, "trivialGuardCeiling");
            var originBias = ClampThreshold(dto.UserOriginBias, DefaultUserOriginBias, MinUserOriginBias, MaxUserOriginBias, "userOriginBias");

            var result = new EffectiveConfig(
                LowCeiling:          lowCeiling,
                BalancedCeiling:     balancedCeiling,
                TrivialGuardCeiling: trivialGuard,
                UserOriginBias:      originBias,
                HighSignalKeywords:  highKeywords,
                LowSignalKeywords:   lowKeywords,
                BalancedFloorKeywords: floorKeywords);

            _logger?.LogInformation(
                "KeywordTierSelector: reloaded config from {Path} " +
                "(lowCeiling={Low}, balancedCeiling={Balanced}, " +
                "trivialGuard={TrivialGuard}, userOriginBias={OriginBias}, " +
                "highSignals={HighCount}, lowSignals={LowCount}, floorKeywords={FloorCount})",
                _configPath, result.LowCeiling, result.BalancedCeiling,
                result.TrivialGuardCeiling, result.UserOriginBias,
                result.HighSignalKeywords.Length, result.LowSignalKeywords.Length,
                result.BalancedFloorKeywords.Length);

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

    private static double ComputeScore(string prompt, int complexSignals, int simplexSignals,
        bool hasCode, bool hasMath, bool hasMultiStep)
    {
        var wordCount = CountWords(prompt);

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

        // No lower clamp: low-signal keywords collected by the dream should be able
        // to push the score negative, actively biasing prompts toward the Low tier.
        return Math.Min(lengthScore + keywordScore + structureScore, 1.0);
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
    /// For high-signal lists, also strips keywords that contain topic/domain words
    /// (matched at word boundaries) that indicate subject matter rather than cognitive complexity.
    /// </summary>
    private string[] SanitizeKeywords(string[] keywords, string listName)
    {
        var isHighSignal = listName.Contains("high", StringComparison.OrdinalIgnoreCase);

        var filtered = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k) && k.Trim().Length >= MinKeywordLength)
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        // For high-signal keywords, strip any keyword that contains a topic/domain word
        // at a word boundary. This catches compound phrases like "reply to email",
        // "schedule meeting", "todo items" where the root topic word is blocked.
        string[] afterBlocklist;
        if (isHighSignal)
        {
            afterBlocklist = filtered
                .Where(k => !ContainsBlockedTopic(k))
                .ToArray();

            var blocked = filtered.Length - afterBlocklist.Length;
            if (blocked > 0)
            {
                var blockedWords = filtered.Where(ContainsBlockedTopic);
                _logger?.LogWarning(
                    "KeywordTierSelector: stripped {Count} topic-containing keyword(s) from {List}: [{Keywords}]",
                    blocked, listName, string.Join(", ", blockedWords.Select(k => $"\"{k}\"")));
            }
        }
        else
        {
            afterBlocklist = filtered;
        }

        var tooShort = keywords.Where(k => string.IsNullOrWhiteSpace(k) || k.Trim().Length < MinKeywordLength).ToArray();
        if (tooShort.Length > 0)
            _logger?.LogWarning(
                "KeywordTierSelector: dropped {Count} keyword(s) from {List} (too short or blank): [{Keywords}]",
                tooShort.Length, listName, string.Join(", ", tooShort.Select(k => $"\"{k}\"")));

        return afterBlocklist;
    }

    /// <summary>
    /// Merges dream-provided keywords with compiled defaults (union, not replace),
    /// then sanitizes the result. Compiled defaults are always preserved.
    /// </summary>
    private string[] MergeKeywords(string[] compiledDefaults, List<string>? dreamKeywords, string listName)
    {
        if (dreamKeywords is null || dreamKeywords.Count == 0)
            return SanitizeKeywords(compiledDefaults, listName);

        // Normalize dream keywords for dedup
        var normalized = dreamKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .ToHashSet();

        // Union: compiled defaults first, then dream additions that aren't already present
        var defaultSet = compiledDefaults.Select(k => k.ToLowerInvariant()).ToHashSet();
        var additions = normalized.Except(defaultSet).ToArray();

        if (additions.Length > 0)
            _logger?.LogInformation(
                "KeywordTierSelector: dream added {Count} keyword(s) to {List}: [{Keywords}]",
                additions.Length, listName, string.Join(", ", additions.Select(k => $"\"{k}\"")));

        var merged = compiledDefaults.Concat(additions).ToArray();
        return SanitizeKeywords(merged, listName);
    }

    /// <summary>
    /// Returns true if the keyword contains any <see cref="TopicBlocklist"/> entry
    /// at a word boundary. This catches both exact matches ("email") and compound
    /// phrases ("reply to email", "schedule meeting").
    /// </summary>
    private static bool ContainsBlockedTopic(string keyword)
    {
        foreach (var topic in TopicBlocklist)
        {
            if (ContainsWholePhrase(keyword, topic))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Clamps a dream-tuned threshold to a guardrail range, logging if clamped.
    /// Returns the compiled default when the value is null.
    /// </summary>
    private double ClampThreshold(double? value, double compiledDefault, double min, double max, string name)
    {
        if (value is null)
            return compiledDefault;

        var clamped = Math.Clamp(value.Value, min, max);
        if (Math.Abs(clamped - value.Value) > 0.001)
            _logger?.LogWarning(
                "KeywordTierSelector: clamped {Name} from {Original:F3} to {Clamped:F3} (allowed range [{Min:F2}, {Max:F2}])",
                name, value.Value, clamped, min, max);

        return clamped;
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
        double TrivialGuardCeiling,
        double UserOriginBias,
        string[] HighSignalKeywords,
        string[] LowSignalKeywords,
        string[] BalancedFloorKeywords);

    private sealed record CachedConfig(EffectiveConfig Config, DateTime LoadedAt);
}
