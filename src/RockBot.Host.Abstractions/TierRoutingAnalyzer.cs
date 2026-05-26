namespace RockBot.Host;

/// <summary>
/// Pure-logic analyzer over a window of <see cref="TierRoutingEntry"/> records.
/// Produces a <see cref="TierRoutingAnalysis"/> containing every deterministic aggregate
/// the downstream LLM would otherwise have to compute itself.
/// <para>
/// No I/O, no DI. Safe to call from agent host code, MCP sidecars, or tests.
/// </para>
/// </summary>
public static class TierRoutingAnalyzer
{
    public const int CurrentSchemaVersion = 1;

    // Defaults match the compiled defaults in KeywordTierSelector — used when the
    // caller's TierSelectorConfig has nulls. Kept here so the analyzer never crashes
    // on a fresh config that hasn't been tuned yet.
    private const double DefaultLowCeiling = 0.15;
    private const double DefaultBalancedCeiling = 0.46;
    private const double ThresholdScanDelta = 0.05;

    // Cluster flagging thresholds — must match the language in routing-dream.md.
    private const int MinClusterSize = 3;
    private const double PanicAvgToolCalls = 3.0;
    private const double TokenSurpriseScoreMax = 0.20;
    private const int TokenSurprisePostInjectionMin = 2000;
    private const long LowOutputAtHighMaxOutputTokens = 200;

    // Keyword candidate filters.
    private const int KeywordMinCount = 5;
    private const double KeywordMinFrequencyRatio = 3.0;
    private const int KeywordMinLength = 4;

    /// <summary>
    /// Run the analyzer over a snapshot of entries. The caller is responsible for
    /// any time-window filtering before calling — this method aggregates whatever it gets.
    /// </summary>
    /// <param name="highTierCostFloorMultiplier">
    /// Routing cost floor. For threshold-scan and flagged-cluster cost projections, the High
    /// tier is priced at no less than this multiple of the Balanced tier's real rate. Prevents
    /// the dream from reading a Balanced→High shift as zero-cost — and ratcheting balancedCeiling
    /// to its floor — when High currently shares Balanced's model. <c>Math.Max</c> means a
    /// genuinely premium High model keeps its real (higher) price. 1.0 disables the floor.
    /// Does NOT affect <see cref="TierRoutingProjectedCost"/>, which always reports real spend.
    /// </param>
    public static TierRoutingAnalysis Analyze(
        IReadOnlyList<TierRoutingEntry> entries,
        TierSelectorConfig? currentConfig = null,
        IReadOnlyList<LlmPricingRow>? pricing = null,
        IReadOnlyDictionary<ModelTier, string?>? tierModelMap = null,
        double highTierCostFloorMultiplier = 1.0)
    {
        var lowCeiling = currentConfig?.LowCeiling ?? DefaultLowCeiling;
        var balancedCeiling = currentConfig?.BalancedCeiling ?? DefaultBalancedCeiling;

        if (entries.Count == 0)
        {
            return new TierRoutingAnalysis(
                SchemaVersion: CurrentSchemaVersion,
                WindowStart: null,
                WindowEnd: null,
                TotalEntries: 0,
                FallbackExcludedCount: 0,
                GlobalStats: BuildGlobalStats([], 0),
                Clusters: [],
                FlaggedClusters: [],
                KeywordCandidates: new([], []),
                ThresholdScans: [],
                ProjectedCost: new(0m, [], 0, 0));
        }

        var quality = entries.Where(e => !e.IsFallbackTriggered).ToList();
        var fallbackCount = entries.Count - quality.Count;

        var globalStats = BuildGlobalStats(entries, fallbackCount);
        var clusters = BuildClusters(quality);
        var flagged = FlagClusters(clusters, pricing, tierModelMap, highTierCostFloorMultiplier);
        var keywordCandidates = BuildKeywordCandidates(quality);
        var scans = BuildThresholdScans(quality, lowCeiling, balancedCeiling, pricing, tierModelMap, highTierCostFloorMultiplier);
        var projectedCost = BuildProjectedCost(entries, pricing);

        return new TierRoutingAnalysis(
            SchemaVersion: CurrentSchemaVersion,
            WindowStart: entries.Min(e => e.Timestamp),
            WindowEnd: entries.Max(e => e.Timestamp),
            TotalEntries: entries.Count,
            FallbackExcludedCount: fallbackCount,
            GlobalStats: globalStats,
            Clusters: clusters,
            FlaggedClusters: flagged,
            KeywordCandidates: keywordCandidates,
            ThresholdScans: scans,
            ProjectedCost: projectedCost);
    }

    // ── Global stats ──────────────────────────────────────────────────────────

    private static TierRoutingGlobalStats BuildGlobalStats(
        IReadOnlyList<TierRoutingEntry> entries, int fallbackCount)
    {
        var total = entries.Count;
        var tiers = new[] { ModelTier.Low, ModelTier.Balanced, ModelTier.High };
        var byTier = tiers.Select(t =>
        {
            var group = entries.Where(e => e.Tier == t).ToList();
            var count = group.Count;
            var pct = total > 0 ? Math.Round(count * 100.0 / total, 1) : 0.0;
            var latency = group.Where(e => e.LatencyMs.HasValue).Select(e => e.LatencyMs!.Value).ToList();
            var input = group.Where(e => e.InputTokens.HasValue).Select(e => e.InputTokens!.Value).ToList();
            var output = group.Where(e => e.OutputTokens.HasValue).Select(e => e.OutputTokens!.Value).ToList();
            var toolCalls = group.Where(e => e.ToolCallCount.HasValue).Select(e => e.ToolCallCount!.Value).ToList();
            return new TierRoutingTierStats(
                Tier: t,
                Count: count,
                Pct: pct,
                AvgComplexityScore: count > 0 ? Math.Round(group.Average(e => e.ComplexityScore), 3) : 0.0,
                AvgLatencyMs: latency.Count > 0 ? (long?)Math.Round(latency.Average()) : null,
                AvgInputTokens: input.Count > 0 ? (long?)Math.Round(input.Average()) : null,
                AvgOutputTokens: output.Count > 0 ? (long?)Math.Round(output.Average()) : null,
                AvgToolCalls: toolCalls.Count > 0 ? Math.Round(toolCalls.Average(), 2) : 0.0);
        }).ToList();

        return new TierRoutingGlobalStats(
            ByTier: byTier,
            UserMessageCount: entries.Count(e => e.Context == "user-message"),
            SubagentCount: entries.Count(e => e.Context == "subagent"),
            FallbackCount: fallbackCount,
            FallbackPct: total > 0 ? Math.Round(fallbackCount * 100.0 / total, 1) : 0.0);
    }

    // ── Clustering ────────────────────────────────────────────────────────────

    private static IReadOnlyList<TierRoutingCluster> BuildClusters(IReadOnlyList<TierRoutingEntry> entries)
    {
        return entries
            .GroupBy(BuildSignature)
            .Select(g =>
            {
                var list = g.ToList();
                var first = list[0];
                var toolCalls = list.Where(e => e.ToolCallCount.HasValue).Select(e => (double)e.ToolCallCount!.Value).ToList();
                var input = list.Where(e => e.InputTokens.HasValue).Select(e => e.InputTokens!.Value).ToList();
                var output = list.Where(e => e.OutputTokens.HasValue).Select(e => e.OutputTokens!.Value).ToList();
                var postInj = list.Where(e => e.PostInjectionTokenEstimate.HasValue)
                    .Select(e => e.PostInjectionTokenEstimate!.Value).ToList();

                return new TierRoutingCluster(
                    Signature: g.Key,
                    Tier: first.Tier,
                    Count: list.Count,
                    SamplePrompt: first.PromptPreview,
                    AvgComplexityScore: Math.Round(list.Average(e => e.ComplexityScore), 3),
                    AvgToolCalls: toolCalls.Count > 0 ? Math.Round(toolCalls.Average(), 2) : 0.0,
                    AvgInputTokens: input.Count > 0 ? (long?)Math.Round(input.Average()) : null,
                    AvgOutputTokens: output.Count > 0 ? (long?)Math.Round(output.Average()) : null,
                    AvgPostInjectionTokens: postInj.Count > 0 ? (int?)Math.Round(postInj.Average()) : null,
                    FallbackCount: list.Count(e => e.IsFallbackTriggered),
                    MatchedHighKeywords: first.MatchedHighKeywords,
                    MatchedLowKeywords: first.MatchedLowKeywords);
            })
            .OrderByDescending(c => c.Count)
            .ToList();
    }

    private static string BuildSignature(TierRoutingEntry e)
    {
        var bucket = ToolCallBucket(e.ToolCallCount);
        if (e.MatchedHighKeywords.Count > 0 || e.MatchedLowKeywords.Count > 0)
        {
            var hi = string.Join(",", e.MatchedHighKeywords.Order(StringComparer.Ordinal));
            var lo = string.Join(",", e.MatchedLowKeywords.Order(StringComparer.Ordinal));
            return $"{e.Tier}|hi={hi};lo={lo}|{bucket}";
        }
        // Fallback: cluster by first 3 normalized words of the prompt preview.
        // Better than dumping every keyword-less prompt into one giant cluster.
        var words = NormalizeWords(e.PromptPreview).Take(3);
        var prefix = string.Join(" ", words);
        return $"{e.Tier}|prefix={prefix}|{bucket}";
    }

    private static string ToolCallBucket(int? count) => count switch
    {
        null or 0 => "tools0",
        1 => "tools1",
        <= 3 => "tools2-3",
        <= 6 => "tools4-6",
        _ => "tools7+"
    };

    // ── Flagging ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<TierRoutingFlaggedCluster> FlagClusters(
        IReadOnlyList<TierRoutingCluster> clusters,
        IReadOnlyList<LlmPricingRow>? pricing,
        IReadOnlyDictionary<ModelTier, string?>? tierModelMap,
        double highTierCostFloorMultiplier)
    {
        var flagged = new List<TierRoutingFlaggedCluster>();

        foreach (var c in clusters)
        {
            if (c.Count < MinClusterSize) continue;

            if (c.Tier == ModelTier.Low && c.AvgToolCalls >= PanicAvgToolCalls)
            {
                flagged.Add(BuildFlagged(c, "panicEscalation",
                    $"Low tier averaging {c.AvgToolCalls:F1} tool calls across {c.Count} entries — model likely struggling.",
                    ModelTier.Balanced, pricing, tierModelMap, highTierCostFloorMultiplier));
                continue;
            }

            if (c.AvgComplexityScore < TokenSurpriseScoreMax
                && c.AvgPostInjectionTokens is int p && p > TokenSurprisePostInjectionMin)
            {
                flagged.Add(BuildFlagged(c, "tokenSurprise",
                    $"Score {c.AvgComplexityScore:F2} but post-injection tokens {p} — context inflation, not user complexity. Informational only.",
                    alternateTier: null, pricing, tierModelMap, highTierCostFloorMultiplier));
                continue;
            }

            if (c.Tier == ModelTier.High
                && c.AvgOutputTokens is long o && o < LowOutputAtHighMaxOutputTokens)
            {
                flagged.Add(BuildFlagged(c, "lowOutputAtHigh",
                    $"High tier producing only {o} avg output tokens across {c.Count} entries — possible over-routing.",
                    ModelTier.Balanced, pricing, tierModelMap, highTierCostFloorMultiplier));
            }
        }

        return flagged;
    }

    private static TierRoutingFlaggedCluster BuildFlagged(
        TierRoutingCluster cluster, string flag, string rationale, ModelTier? alternateTier,
        IReadOnlyList<LlmPricingRow>? pricing, IReadOnlyDictionary<ModelTier, string?>? tierModelMap,
        double highTierCostFloorMultiplier)
    {
        decimal? currentCost = null;
        decimal? alternateCost = null;
        if (pricing is not null && tierModelMap is not null
            && cluster.AvgInputTokens is long ai && cluster.AvgOutputTokens is long ao)
        {
            var perCallCurrent = TryPriceCallForTier(cluster.Tier, ai, ao, tierModelMap, pricing, highTierCostFloorMultiplier);
            currentCost = perCallCurrent * cluster.Count;
            if (alternateTier is ModelTier alt)
            {
                var perCallAlt = TryPriceCallForTier(alt, ai, ao, tierModelMap, pricing, highTierCostFloorMultiplier);
                alternateCost = perCallAlt * cluster.Count;
            }
        }

        return new TierRoutingFlaggedCluster(cluster, flag, rationale,
            currentCost, alternateCost, alternateTier);
    }

    // ── Keyword candidates ────────────────────────────────────────────────────

    private static TierRoutingKeywordCandidates BuildKeywordCandidates(IReadOnlyList<TierRoutingEntry> entries)
    {
        // Exclude any token that already triggers a routing signal — we want NEW candidates.
        var alreadyMatched = new HashSet<string>(
            entries.SelectMany(e => e.MatchedHighKeywords.Concat(e.MatchedLowKeywords))
                   .Select(k => k.ToLowerInvariant()),
            StringComparer.Ordinal);

        var counts = new Dictionary<string, (int hi, int bal, int lo)>(StringComparer.Ordinal);

        foreach (var e in entries)
        {
            foreach (var token in NormalizeWords(e.PromptPreview).Distinct(StringComparer.Ordinal))
            {
                if (token.Length < KeywordMinLength) continue;
                if (alreadyMatched.Contains(token)) continue;

                var current = counts.GetValueOrDefault(token);
                counts[token] = e.Tier switch
                {
                    ModelTier.High => (current.hi + 1, current.bal, current.lo),
                    ModelTier.Balanced => (current.hi, current.bal + 1, current.lo),
                    _ => (current.hi, current.bal, current.lo + 1)
                };
            }
        }

        var highCandidates = new List<TierRoutingKeywordCandidate>();
        var lowCandidates = new List<TierRoutingKeywordCandidate>();

        foreach (var (token, (hi, bal, lo)) in counts)
        {
            var total = hi + bal + lo;
            if (total < KeywordMinCount) continue;

            var highDenom = Math.Max(1, bal + lo);
            var highRatio = (double)hi / highDenom;
            if (hi >= KeywordMinCount && highRatio >= KeywordMinFrequencyRatio)
            {
                highCandidates.Add(new TierRoutingKeywordCandidate(token, hi, bal, lo, Math.Round(highRatio, 2)));
                continue;
            }

            var lowDenom = Math.Max(1, hi + bal);
            var lowRatio = (double)lo / lowDenom;
            if (lo >= KeywordMinCount && lowRatio >= KeywordMinFrequencyRatio)
            {
                lowCandidates.Add(new TierRoutingKeywordCandidate(token, hi, bal, lo, Math.Round(lowRatio, 2)));
            }
        }

        return new TierRoutingKeywordCandidates(
            HighSignalCandidates: highCandidates.OrderByDescending(c => c.FrequencyRatio).ToList(),
            LowSignalCandidates: lowCandidates.OrderByDescending(c => c.FrequencyRatio).ToList());
    }

    private static IEnumerable<string> NormalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    // ── Threshold scans ───────────────────────────────────────────────────────

    private static IReadOnlyList<TierRoutingThresholdScan> BuildThresholdScans(
        IReadOnlyList<TierRoutingEntry> entries,
        double lowCeiling, double balancedCeiling,
        IReadOnlyList<LlmPricingRow>? pricing,
        IReadOnlyDictionary<ModelTier, string?>? tierModelMap,
        double highTierCostFloorMultiplier)
    {
        return new[]
        {
            BuildScan(entries, "lowCeiling", +ThresholdScanDelta, lowCeiling, balancedCeiling, pricing, tierModelMap, highTierCostFloorMultiplier),
            BuildScan(entries, "lowCeiling", -ThresholdScanDelta, lowCeiling, balancedCeiling, pricing, tierModelMap, highTierCostFloorMultiplier),
            BuildScan(entries, "balancedCeiling", +ThresholdScanDelta, lowCeiling, balancedCeiling, pricing, tierModelMap, highTierCostFloorMultiplier),
            BuildScan(entries, "balancedCeiling", -ThresholdScanDelta, lowCeiling, balancedCeiling, pricing, tierModelMap, highTierCostFloorMultiplier)
        };
    }

    private static TierRoutingThresholdScan BuildScan(
        IReadOnlyList<TierRoutingEntry> entries,
        string threshold, double delta,
        double lowCeiling, double balancedCeiling,
        IReadOnlyList<LlmPricingRow>? pricing,
        IReadOnlyDictionary<ModelTier, string?>? tierModelMap,
        double highTierCostFloorMultiplier)
    {
        // Round shifted thresholds to 4 decimals — bare double subtraction yields
        // 0.15 - 0.05 = 0.09999999999999998, which makes boundary entries (score 0.10)
        // appear to flip when they wouldn't with the actual selector's <=  comparison.
        // Config values are at most 2 decimal places, so 4 is plenty of precision.
        var newLow = threshold == "lowCeiling" ? Math.Round(lowCeiling + delta, 4) : lowCeiling;
        var newBal = threshold == "balancedCeiling" ? Math.Round(balancedCeiling + delta, 4) : balancedCeiling;
        var flips = new List<(TierRoutingEntry e, ModelTier from, ModelTier to)>();

        // Compare simulated(original) vs simulated(shifted) — both apply pure threshold
        // logic, so the diff isolates the threshold shift's impact from selector guards
        // (trivial guard, user-origin bias) that affected the recorded e.Tier.
        foreach (var e in entries)
        {
            var simulatedOriginal = SimulateTier(e.ComplexityScore, lowCeiling, balancedCeiling);
            var simulatedShifted = SimulateTier(e.ComplexityScore, newLow, newBal);
            if (simulatedShifted != simulatedOriginal)
                flips.Add((e, simulatedOriginal, simulatedShifted));
        }

        decimal? costDelta = null;
        if (pricing is not null && tierModelMap is not null)
        {
            decimal accum = 0m;
            var any = false;
            foreach (var (e, from, to) in flips)
            {
                if (e.InputTokens is not long it || e.OutputTokens is not long ot) continue;
                var fromCost = TryPriceCallForTier(from, it, ot, tierModelMap, pricing, highTierCostFloorMultiplier);
                var toCost = TryPriceCallForTier(to, it, ot, tierModelMap, pricing, highTierCostFloorMultiplier);
                if (fromCost is null || toCost is null) continue;
                accum += toCost.Value - fromCost.Value;
                any = true;
            }
            if (any) costDelta = accum;
        }

        var directions = flips.GroupBy(f => $"{f.from}->{f.to}")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ({g.Count()})");

        return new TierRoutingThresholdScan(
            Threshold: threshold,
            Delta: delta,
            EntriesFlipped: flips.Count,
            DirectionDescription: string.Join(", ", directions),
            SamplePrompts: flips.Take(5).Select(f => f.e.PromptPreview).ToList(),
            ProjectedCostDelta: costDelta);
    }

    private static ModelTier SimulateTier(double score, double lowCeiling, double balancedCeiling)
    {
        if (score <= lowCeiling) return ModelTier.Low;
        if (score <= balancedCeiling) return ModelTier.Balanced;
        return ModelTier.High;
    }

    // ── Projected cost ────────────────────────────────────────────────────────

    private static TierRoutingProjectedCost BuildProjectedCost(
        IReadOnlyList<TierRoutingEntry> entries, IReadOnlyList<LlmPricingRow>? pricing)
    {
        if (pricing is null || pricing.Count == 0)
            return new TierRoutingProjectedCost(0m, [], 0, entries.Count);

        decimal total = 0m;
        var byTier = new Dictionary<ModelTier, decimal>();
        var priced = 0;
        var unpriced = 0;

        foreach (var e in entries)
        {
            if (e.InputTokens is not long it || e.OutputTokens is not long ot)
            {
                unpriced++;
                continue;
            }
            var cost = TryPriceCall(e.ModelId, it, ot, pricing);
            if (cost is null)
            {
                unpriced++;
                continue;
            }
            total += cost.Value;
            byTier[e.Tier] = byTier.GetValueOrDefault(e.Tier) + cost.Value;
            priced++;
        }

        var byTierList = byTier
            .Select(kv => new TierRoutingTierCost(kv.Key, Math.Round(kv.Value, 4)))
            .OrderBy(c => c.Tier)
            .ToList();

        return new TierRoutingProjectedCost(
            TotalUsd: Math.Round(total, 4),
            ByTier: byTierList,
            EntriesPricedCount: priced,
            EntriesUnpricedCount: unpriced);
    }

    /// <summary>
    /// Prices a call by raw model ID at real pricing — no cost floor. Used for
    /// <see cref="BuildProjectedCost"/>, which reports actual spend.
    /// </summary>
    private static decimal? TryPriceCall(string? modelId, long inputTokens, long outputTokens, IReadOnlyList<LlmPricingRow> pricing)
    {
        var row = MatchRow(modelId, pricing);
        if (row is null) return null;
        return (inputTokens * row.InputPerM + outputTokens * row.OutputPerM) / 1_000_000m;
    }

    /// <summary>
    /// Prices a call for a specific <see cref="ModelTier"/> with the routing cost floor applied
    /// (see <see cref="EffectiveTierRates"/>). Used by the threshold-scan and flagged-cluster
    /// projections that drive tuning decisions — NOT by real-spend reporting.
    /// </summary>
    private static decimal? TryPriceCallForTier(
        ModelTier tier, long inputTokens, long outputTokens,
        IReadOnlyDictionary<ModelTier, string?> tierModelMap,
        IReadOnlyList<LlmPricingRow> pricing,
        double highTierCostFloorMultiplier)
    {
        var rates = EffectiveTierRates(tier, tierModelMap, pricing, highTierCostFloorMultiplier);
        if (rates is null) return null;
        var (inPerM, outPerM) = rates.Value;
        return (inputTokens * inPerM + outputTokens * outPerM) / 1_000_000m;
    }

    /// <summary>
    /// Effective per-million-token rates for a tier. For the High tier, rates are floored at
    /// <paramref name="highTierCostFloorMultiplier"/> × the Balanced tier's real rates so a
    /// Balanced→High shift never projects as zero-cost when High shares Balanced's model.
    /// <c>Math.Max</c> keeps a genuinely premium High model at its real (higher) price.
    /// Returns <c>null</c> when the tier's model has no pricing row.
    /// </summary>
    private static (decimal InputPerM, decimal OutputPerM)? EffectiveTierRates(
        ModelTier tier,
        IReadOnlyDictionary<ModelTier, string?> tierModelMap,
        IReadOnlyList<LlmPricingRow> pricing,
        double highTierCostFloorMultiplier)
    {
        var row = MatchRow(tierModelMap.GetValueOrDefault(tier), pricing);
        if (row is null) return null;

        if (tier == ModelTier.High && highTierCostFloorMultiplier > 1.0)
        {
            var balanced = MatchRow(tierModelMap.GetValueOrDefault(ModelTier.Balanced), pricing);
            if (balanced is not null)
            {
                var m = (decimal)highTierCostFloorMultiplier;
                return (Math.Max(row.InputPerM, balanced.InputPerM * m),
                        Math.Max(row.OutputPerM, balanced.OutputPerM * m));
            }
        }

        return (row.InputPerM, row.OutputPerM);
    }

    private static LlmPricingRow? MatchRow(string? modelId, IReadOnlyList<LlmPricingRow> pricing)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        return pricing.FirstOrDefault(p => modelId.Contains(p.Prefix, StringComparison.OrdinalIgnoreCase));
    }
}
