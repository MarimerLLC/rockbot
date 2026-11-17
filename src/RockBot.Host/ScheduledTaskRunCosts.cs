using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>Options for <see cref="ScheduledTaskRunCosts"/> and the dream's scheduled-task tier pass (#846).</summary>
public sealed class ScheduledTaskCostOptions
{
    /// <summary>The run-history file. When relative, resolved under <see cref="AgentProfileOptions.BasePath"/>.</summary>
    public string FilePath { get; set; } = "scheduled-task-runs.json";

    /// <summary>Runs kept per task.</summary>
    public int MaxRunsPerTask { get; set; } = 20;

    /// <summary>How many of a task's latest runs make its baseline (trailing median).</summary>
    public int BaselineRuns { get; set; } = 8;

    /// <summary>Runs a task needs before a run can be called unusually expensive.</summary>
    public int MinBaselineRuns { get; set; } = 3;

    /// <summary>A run costing at least this many times the baseline median is flagged.</summary>
    public double AnomalyMultiplier { get; set; } = 3.0;

    /// <summary>Runs cheaper than this are never flagged, however they compare to a tiny baseline.</summary>
    public double MinAnomalyCostUsd { get; set; } = 0.25;

    /// <summary>Whether the dream may set a task's <see cref="ScheduledTask.MaxTier"/> from its run history.</summary>
    public bool AutoTierCeiling { get; set; } = true;

    /// <summary>Runs that finished on Balanced or lower a task needs before the dream will cap it.</summary>
    public int TierCeilingMinBalancedRuns { get; set; } = 3;

    /// <summary>The dream caps a task when its High runs' median cost is at least this many times its Balanced runs'.</summary>
    public double TierCeilingCostRatio { get; set; } = 2.0;
}

/// <summary>One finished run of a scheduled task.</summary>
/// <param name="HighestTier">The highest tier the run's patrol turn or any of its subagents ran on; null when unknown.</param>
/// <param name="Anomalous">True when the run was flagged as unusually expensive.</param>
public sealed record ScheduledRunRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double CostUsd,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ModelTier? HighestTier,
    bool Anomalous = false);

/// <summary>How a finished run's cost compares with the task's recent runs.</summary>
/// <param name="CostUsd">The run's estimated cost.</param>
/// <param name="BaselineMedianUsd">The median cost of the runs before it; null with too few runs.</param>
/// <param name="BaselineRuns">How many runs the median is over.</param>
/// <param name="Anomalous">True when the run cost far more than the baseline.</param>
public sealed record ScheduledRunCostCheck(double CostUsd, double? BaselineMedianUsd, int BaselineRuns, bool Anomalous)
{
    /// <summary>A line for the run's saved response when it was flagged; otherwise null.</summary>
    public string? Note => Anomalous && BaselineMedianUsd is { } median
        ? string.Create(CultureInfo.InvariantCulture,
            $"_Cost note: this run cost about ${CostUsd:0.00}, {CostUsd / median:0.#}× this task's usual ${median:0.00} " +
            $"(median of its last {BaselineRuns} runs)._")
        : null;

    /// <summary><paramref name="content"/> with <see cref="Note"/> appended when there is one.</summary>
    public string Annotate(string content) => Note is { } note ? $"{content.TrimEnd()}\n\n---\n{note}" : content;
}

/// <summary>A task's recent runs, for the Usage tab.</summary>
public sealed record ScheduledTaskCostSummary(
    string TaskName,
    int Runs,
    ScheduledRunRecord LastRun,
    double? MedianCostUsd);

/// <summary>
/// The cost of each scheduled-task run (#846). A run starts when the task fires and ends when its
/// report is saved — after its subagents' synthesis when it delegated. Its cost is the difference
/// in <see cref="LlmUsageLedger.ScheduledTaskCostToDate"/> across the run, so it covers the patrol
/// turn and everything it started. Finished runs are kept per task in a small JSON file, so a run
/// can be compared with the task's trailing median (the cost guard) and the dream can see which
/// tier each run used (<see cref="ScheduledTaskTierAdvisor"/>).
/// </summary>
/// <remarks>
/// Open runs live in memory only. A restart mid-run, or a delegated run whose synthesis never
/// happens, loses that run; the next fire starts a fresh one.
/// </remarks>
public sealed class ScheduledTaskRunCosts
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Func<string, double>? _costToDate;
    private readonly ScheduledTaskCostOptions _options;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, OpenRun> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private Dictionary<string, List<ScheduledRunRecord>>? _history;

    public ScheduledTaskRunCosts(
        IOptions<ScheduledTaskCostOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<ScheduledTaskRunCosts> logger,
        LlmUsageLedger? ledger = null)
        : this(ledger is null ? null : ledger.ScheduledTaskCostToDate, options.Value,
            ResolvePath(options.Value.FilePath, profileOptions.Value.BasePath), logger, () => DateTimeOffset.UtcNow)
    {
    }

    internal ScheduledTaskRunCosts(
        Func<string, double>? costToDate,
        ScheduledTaskCostOptions options,
        string path,
        ILogger? logger,
        Func<DateTimeOffset> now)
    {
        _costToDate = costToDate;
        _options = options;
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        _now = now;
    }

    /// <summary>Starts a run of <paramref name="taskName"/>, whose patrol turn runs on <paramref name="tier"/>.</summary>
    public void BeginRun(string taskName, ModelTier tier)
    {
        if (_costToDate is null)
            return;
        _open[taskName] = new OpenRun(_now(), _costToDate(taskName), tier);
    }

    /// <summary>Notes that the open run of <paramref name="taskName"/> used <paramref name="tier"/> (a subagent's tier).</summary>
    public void NoteTier(string taskName, ModelTier tier)
    {
        if (_open.TryGetValue(taskName, out var run))
            run.Raise(tier);
    }

    /// <summary>
    /// Ends the open run of <paramref name="taskName"/>, records it, and compares its cost with the
    /// task's trailing median. Null when no run was open (or there is no ledger).
    /// </summary>
    public async Task<ScheduledRunCostCheck?> EndRunAsync(string taskName, CancellationToken ct = default)
    {
        if (_costToDate is null || !_open.TryRemove(taskName, out var run))
            return null;

        var cost = Math.Max(0, _costToDate(taskName) - run.StartCost);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await LoadAsync(ct).ConfigureAwait(false);
            if (!history.TryGetValue(taskName, out var runs))
                history[taskName] = runs = [];

            var check = Evaluate(cost, runs, _options);
            runs.Add(new ScheduledRunRecord(run.StartedAt, _now(), cost, run.HighestTier, check.Anomalous));
            if (runs.Count > _options.MaxRunsPerTask)
                runs.RemoveRange(0, runs.Count - _options.MaxRunsPerTask);
            await SaveAsync(history, ct).ConfigureAwait(false);

            if (check.Anomalous)
                _logger.LogWarning(
                    "Scheduled task '{Task}' run cost ${Cost:F2}, {Ratio:F1}x its median ${Median:F2} over {Runs} runs (highest tier {Tier})",
                    taskName, cost, cost / check.BaselineMedianUsd!.Value, check.BaselineMedianUsd, check.BaselineRuns, run.HighestTier);
            else
                _logger.LogInformation("Scheduled task '{Task}' run cost ${Cost:F2} (highest tier {Tier})",
                    taskName, cost, run.HighestTier);
            return check;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>The recorded runs of <paramref name="taskName"/>, oldest first.</summary>
    public async Task<IReadOnlyList<ScheduledRunRecord>> GetRunsAsync(string taskName, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await LoadAsync(ct).ConfigureAwait(false);
            return history.TryGetValue(taskName, out var runs) ? [.. runs] : [];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Each task's latest run and trailing median, most recently run first.</summary>
    public async Task<IReadOnlyList<ScheduledTaskCostSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await LoadAsync(ct).ConfigureAwait(false);
            return
            [
                .. history
                    .Where(h => h.Value.Count > 0)
                    .Select(h => new ScheduledTaskCostSummary(
                        h.Key, h.Value.Count, h.Value[^1],
                        Median(h.Value.TakeLast(_options.BaselineRuns).Select(r => r.CostUsd))))
                    .OrderByDescending(s => s.LastRun.EndedAt),
            ];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>How <paramref name="cost"/> compares with the latest <paramref name="prior"/> runs.</summary>
    internal static ScheduledRunCostCheck Evaluate(double cost, IReadOnlyList<ScheduledRunRecord> prior, ScheduledTaskCostOptions options)
    {
        var baseline = prior.TakeLast(options.BaselineRuns).Select(r => r.CostUsd).ToList();
        if (baseline.Count < options.MinBaselineRuns)
            return new ScheduledRunCostCheck(cost, null, baseline.Count, Anomalous: false);

        // A zero median (unpriced or free runs) gives nothing to compare against.
        var median = Median(baseline)!.Value;
        if (median <= 0)
            return new ScheduledRunCostCheck(cost, null, baseline.Count, Anomalous: false);

        var anomalous = cost >= options.MinAnomalyCostUsd && cost >= options.AnomalyMultiplier * median;
        return new ScheduledRunCostCheck(cost, median, baseline.Count, anomalous);
    }

    internal static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private async Task<Dictionary<string, List<ScheduledRunRecord>>> LoadAsync(CancellationToken ct)
    {
        if (_history is not null)
            return _history;

        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                var file = await JsonSerializer.DeserializeAsync<HistoryFile>(stream, JsonOptions, ct).ConfigureAwait(false);
                return _history = new Dictionary<string, List<ScheduledRunRecord>>(
                    file?.Tasks ?? [], StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An estimate for display and the dream; a bad file is set aside rather than failing a run.
            _logger.LogWarning(ex, "Could not read scheduled-task run costs at {Path}; starting a new history", _path);
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }

        return _history = new Dictionary<string, List<ScheduledRunRecord>>(StringComparer.Ordinal);
    }

    private async Task SaveAsync(Dictionary<string, List<ScheduledRunRecord>> history, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, new HistoryFile { Tasks = history }, JsonOptions, ct).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write scheduled-task run costs to {Path}", _path);
        }
    }

    private static string ResolvePath(string path, string basePath) =>
        Path.IsPathRooted(path) ? path : Path.Combine(basePath, path);

    private sealed class HistoryFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, List<ScheduledRunRecord>> Tasks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class OpenRun(DateTimeOffset startedAt, double startCost, ModelTier tier)
    {
        private int _highest = (int)tier;

        public DateTimeOffset StartedAt { get; } = startedAt;
        public double StartCost { get; } = startCost;
        public ModelTier HighestTier => (ModelTier)Volatile.Read(ref _highest);

        public void Raise(ModelTier tier)
        {
            int current;
            while ((int)tier > (current = Volatile.Read(ref _highest))
                   && Interlocked.CompareExchange(ref _highest, (int)tier, current) != current)
            {
            }
        }
    }
}
