using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Persists cumulative LLM cost/token totals for <see cref="DreamService"/>'s background
/// passes, which otherwise have zero cost visibility — unlike scheduled tasks (see
/// <c>IScheduledTaskStore.RecordRunAsync</c>), DreamService runs on its own 12-hour cron and
/// is not itself a scheduled task, so nothing records what its cycles actually cost.
/// </summary>
/// <remarks>
/// Persisted next to <see cref="DreamPassLedger"/> so it survives a pod restart; a lost or
/// corrupt file degrades to "lifetime totals reset to zero", never to a crashed dream cycle —
/// cost bookkeeping must not be able to break the thing it is measuring.
/// </remarks>
internal sealed class DreamCostTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>On-disk shape. All fields additive-only per the project's schema convention.</summary>
    internal sealed record State(
        long TotalCycles,
        long TotalPassRuns,
        long TotalInputTokens,
        long TotalOutputTokens,
        long TotalCachedInputTokens,
        double TotalCostUsd,
        DateTimeOffset? LastCycleAt,
        long LastCyclePassRuns,
        long LastCycleInputTokens,
        long LastCycleOutputTokens,
        long LastCycleCachedInputTokens,
        double LastCycleCostUsd);

    /// <summary>Default filename, relative to <see cref="AgentProfileOptions.BasePath"/>.</summary>
    internal const string FileName = "dream-service-cost.json";

    private readonly string _path;
    private readonly ILogger _logger;
    private State _lifetime;

    // Accumulates the cycle currently in progress; folded into _lifetime's "LastCycle*"
    // fields (and added to the lifetime totals) when the cycle finishes.
    private long _cyclePassRuns;
    private long _cycleInputTokens;
    private long _cycleOutputTokens;
    private long _cycleCachedInputTokens;
    private double _cycleCostUsd;
    private bool _dirty;

    private DreamCostTracker(string path, State lifetime, ILogger logger)
    {
        _path = path;
        _lifetime = lifetime;
        _logger = logger;
    }

    public State Lifetime => _lifetime;

    /// <summary>
    /// Reads the tracker from disk. A missing, unreadable, or malformed file yields a
    /// zeroed tracker rather than an exception: the cost of forgetting is losing the running
    /// total, and the dream cycle must not fail over bookkeeping.
    /// </summary>
    public static async Task<DreamCostTracker> LoadAsync(string path, ILogger logger)
    {
        var state = new State(0, 0, 0, 0, 0, 0.0, null, 0, 0, 0, 0, 0.0);

        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<State>(json, JsonOptions);
                if (loaded is not null)
                    state = loaded;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DreamCostTracker: failed to read {Path}; lifetime totals reset to zero", path);
            }
        }

        return new DreamCostTracker(path, state, logger);
    }

    /// <summary>Records one dream pass's LLM call. Call once per <c>InvokeDreamPassAsync</c> call that produced usage.</summary>
    public void RecordPass(long inputTokens, long outputTokens, long cachedInputTokens, double costUsd)
    {
        _cyclePassRuns++;
        _cycleInputTokens += inputTokens;
        _cycleOutputTokens += outputTokens;
        _cycleCachedInputTokens += cachedInputTokens;
        _cycleCostUsd += costUsd;
        _dirty = true;
    }

    /// <summary>
    /// Folds the in-progress cycle's totals into the lifetime totals and the "last cycle"
    /// snapshot, then resets the in-progress accumulator. Safe to call even when the cycle
    /// recorded zero passes (e.g. every pass was gate-skipped) — it still advances
    /// <see cref="State.TotalCycles"/> and clears the previous "last cycle" snapshot forward.
    /// </summary>
    public void FinishCycle(DateTimeOffset now)
    {
        _lifetime = _lifetime with
        {
            TotalCycles = _lifetime.TotalCycles + 1,
            TotalPassRuns = _lifetime.TotalPassRuns + _cyclePassRuns,
            TotalInputTokens = _lifetime.TotalInputTokens + _cycleInputTokens,
            TotalOutputTokens = _lifetime.TotalOutputTokens + _cycleOutputTokens,
            TotalCachedInputTokens = _lifetime.TotalCachedInputTokens + _cycleCachedInputTokens,
            TotalCostUsd = _lifetime.TotalCostUsd + _cycleCostUsd,
            LastCycleAt = now,
            LastCyclePassRuns = _cyclePassRuns,
            LastCycleInputTokens = _cycleInputTokens,
            LastCycleOutputTokens = _cycleOutputTokens,
            LastCycleCachedInputTokens = _cycleCachedInputTokens,
            LastCycleCostUsd = _cycleCostUsd
        };
        _cyclePassRuns = 0;
        _cycleInputTokens = 0;
        _cycleOutputTokens = 0;
        _cycleCachedInputTokens = 0;
        _cycleCostUsd = 0.0;
        _dirty = true;
    }

    /// <summary>
    /// Writes the tracker back to disk if anything changed. Uses <see cref="AtomicFile"/> so a
    /// crash mid-write cannot leave a truncated file behind.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (!_dirty) return;

        try
        {
            await AtomicFile.WriteAllTextAsync(
                _path, JsonSerializer.Serialize(_lifetime, JsonOptions), ct).ConfigureAwait(false);
            _dirty = false;
        }
        catch (Exception ex)
        {
            // A tracker that fails to persist costs one lost cycle of totals next time. Never fatal.
            _logger.LogWarning(ex, "DreamCostTracker: failed to write {Path}", _path);
        }
    }
}
