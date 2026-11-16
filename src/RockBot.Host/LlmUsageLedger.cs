using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>Options for <see cref="LlmUsageLedger"/> (#834).</summary>
public sealed class LlmUsageOptions
{
    /// <summary>The ledger file. When relative, resolved under <see cref="AgentProfileOptions.BasePath"/>.</summary>
    public string FilePath { get; set; } = "usage-ledger.json";

    /// <summary>Days of daily totals kept.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Threads kept; the least recently active go first. Threads idle past the retention go too.</summary>
    public int MaxThreads { get; set; } = 500;

    /// <summary>How often pending usage is written to the file.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Token and cost totals for a set of LLM requests. Cost is the agent's estimate from its pricing table.</summary>
public sealed class LlmUsageTotals
{
    public long Requests { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public double CostUsd { get; set; }

    /// <summary>Requests that used tokens but matched no pricing row, so were costed at $0.</summary>
    public long UnpricedRequests { get; set; }

    internal void Add(LlmUsageTotals other)
    {
        Requests += other.Requests;
        InputTokens += other.InputTokens;
        CachedInputTokens += other.CachedInputTokens;
        CacheWriteTokens += other.CacheWriteTokens;
        OutputTokens += other.OutputTokens;
        CostUsd += other.CostUsd;
        UnpricedRequests += other.UnpricedRequests;
    }

    internal LlmUsageTotals Clone()
    {
        var copy = new LlmUsageTotals();
        copy.Add(this);
        return copy;
    }
}

/// <summary>One day's usage, in the agent's home time zone.</summary>
public sealed class LlmUsageDay
{
    public LlmUsageTotals Totals { get; set; } = new();

    /// <summary>By the model that answered.</summary>
    public Dictionary<string, LlmUsageTotals> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// By workload: <c>session</c> (conversations), <c>patrol</c> (scheduled tasks), <c>subagent</c>,
    /// <c>worker</c>, or <c>background</c> (requests outside any run: dreams, memory audits).
    /// </summary>
    public Dictionary<string, LlmUsageTotals> ByKind { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One thread's usage: its own turns plus the subagents and workers it started.</summary>
public sealed class LlmUsageThread
{
    public DateTimeOffset FirstAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public LlmUsageTotals Totals { get; set; } = new();
}

/// <summary>The ledger file's contents.</summary>
public sealed class LlmUsageLedgerFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>When the ledger recorded its first request.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>By date (<c>yyyy-MM-dd</c>, home time zone).</summary>
    public Dictionary<string, LlmUsageDay> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>By raw thread id.</summary>
    public Dictionary<string, LlmUsageThread> Threads { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What <see cref="LlmUsageLedger.GetReport"/> answers.</summary>
public sealed record LlmUsageReport(
    string TimeZone,
    DateTimeOffset? Since,
    string Today,
    LlmUsageTotals TodayTotals,
    LlmUsageTotals Last7Days,
    LlmUsageTotals Last30Days,
    IReadOnlyList<(string Date, LlmUsageTotals Totals)> Days,
    IReadOnlyList<(string ModelId, LlmUsageTotals Totals)> Models30Days,
    IReadOnlyList<(string Kind, LlmUsageTotals Totals)> Kinds30Days,
    string? ThreadId,
    LlmUsageThread? Thread);

/// <summary>
/// Running totals of the LLM requests the agent sends (#834): tokens and estimated cost per day,
/// per model, per workload, and per conversation thread, persisted so they survive a restart.
/// Fed by <see cref="RequestAccountingChatClient"/>, which sees each provider request once.
/// </summary>
/// <remarks>
/// Days are counted in the agent's home time zone (<see cref="AgentClock.HomeZone"/>), so a
/// temporary travel zone doesn't split a day. A request is charged to the thread that caused it:
/// a subagent's or worker's requests count toward the conversation that started it. Writes are
/// batched (<see cref="LlmUsageOptions.FlushInterval"/>); the last few seconds before a crash can
/// be lost, which an estimate can afford.
/// </remarks>
public sealed class LlmUsageLedger : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Lock _lock = new();
    private readonly LlmCostEstimator _costEstimator;
    private readonly Lazy<AgentClock?> _clock;
    private readonly LlmUsageOptions _options;
    private readonly ILogger<LlmUsageLedger> _logger;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Timer? _flushTimer;
    private LlmUsageLedgerFile _file;
    private bool _dirty;

    public LlmUsageLedger(
        LlmCostEstimator costEstimator,
        IOptions<LlmUsageOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<LlmUsageLedger> logger,
        IServiceProvider? services = null)
        : this(costEstimator, options.Value, ResolvePath(options.Value.FilePath, profileOptions.Value.BasePath),
            logger, new Lazy<AgentClock?>(() => services is null ? null : ResolveClock(services, logger)),
            () => DateTimeOffset.UtcNow, startTimer: true)
    {
    }

    internal LlmUsageLedger(
        LlmCostEstimator costEstimator,
        LlmUsageOptions options,
        string path,
        ILogger<LlmUsageLedger> logger,
        AgentClock? clock,
        Func<DateTimeOffset> now,
        bool startTimer)
        : this(costEstimator, options, path, logger, new Lazy<AgentClock?>(clock), now, startTimer)
    {
    }

    private LlmUsageLedger(
        LlmCostEstimator costEstimator,
        LlmUsageOptions options,
        string path,
        ILogger<LlmUsageLedger> logger,
        Lazy<AgentClock?> clock,
        Func<DateTimeOffset> now,
        bool startTimer)
    {
        _costEstimator = costEstimator;
        _options = options;
        _path = path;
        _logger = logger;
        _clock = clock;
        _now = now;
        _file = Load();
        if (startTimer)
            _flushTimer = new Timer(_ => Flush(), null, options.FlushInterval, options.FlushInterval);
    }

    /// <summary>The zone days are counted in.</summary>
    public TimeZoneInfo Zone => _clock.Value?.HomeZone ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The agent's clock, resolved on first use rather than injected: a host without the clock's
    /// own dependencies (a test host, a sidecar) still gets a ledger, counting days in UTC.
    /// </summary>
    private static AgentClock? ResolveClock(IServiceProvider services, ILogger logger)
    {
        try
        {
            return services.GetService(typeof(AgentClock)) as AgentClock;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "No agent clock; usage days are counted in UTC");
            return null;
        }
    }

    /// <summary>
    /// Records one provider request. <paramref name="runSessionId"/> is the session of the run
    /// that sent it (it gives the workload kind); <paramref name="threadId"/> is the conversation
    /// it is charged to.
    /// </summary>
    public void Record(string? runSessionId, string? threadId, string modelId, UsageDetails? usage)
    {
        if (usage is null)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        if (input == 0 && output == 0)
            return;

        var cost = _costEstimator.EstimateCost(modelId, usage);
        var request = new LlmUsageTotals
        {
            Requests = 1,
            InputTokens = input,
            CachedInputTokens = UsageReader.GetCachedInputTokens(usage),
            CacheWriteTokens = UsageReader.GetCacheWriteTokens(usage),
            OutputTokens = output,
            CostUsd = cost,
            UnpricedRequests = cost > 0 ? 0 : 1,
        };

        var now = _now();
        var day = DayKey(now);
        // A request outside any run (the dream pass, memory audits) is the agent's own background work.
        var kind = runSessionId is null ? "background" : AgentLoopRunner.ClassifySessionKind(runSessionId);
        var thread = string.IsNullOrEmpty(threadId)
            ? (runSessionId is null ? null : SessionIds.Raw(runSessionId))
            : threadId;

        lock (_lock)
        {
            _file.Since ??= now;
            if (!_file.Days.TryGetValue(day, out var d))
                _file.Days[day] = d = new LlmUsageDay();
            d.Totals.Add(request);
            Bucket(d.ByModel, string.IsNullOrEmpty(modelId) ? "(unknown)" : modelId).Add(request);
            Bucket(d.ByKind, kind).Add(request);

            if (thread is not null)
            {
                if (!_file.Threads.TryGetValue(thread, out var t))
                    _file.Threads[thread] = t = new LlmUsageThread { FirstAt = now };
                t.LastAt = now;
                t.Totals.Add(request);
            }

            _dirty = true;
        }
    }

    /// <summary>The totals for today, the last 7 and 30 days, each day, each model, each workload, and <paramref name="threadId"/>.</summary>
    public LlmUsageReport GetReport(string? threadId)
    {
        var now = _now();
        var today = DayKey(now);
        var todayDate = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            var last7 = new LlmUsageTotals();
            var last30 = new LlmUsageTotals();
            var models = new Dictionary<string, LlmUsageTotals>(StringComparer.OrdinalIgnoreCase);
            var kinds = new Dictionary<string, LlmUsageTotals>(StringComparer.Ordinal);
            var days = new List<(string, LlmUsageTotals)>();

            foreach (var (key, day) in _file.Days.OrderByDescending(d => d.Key, StringComparer.Ordinal))
            {
                if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                var age = todayDate.DayNumber - date.DayNumber;
                if (age < 0 || age >= 30)
                    continue;

                days.Add((key, day.Totals.Clone()));
                last30.Add(day.Totals);
                if (age < 7)
                    last7.Add(day.Totals);
                foreach (var (model, totals) in day.ByModel)
                    Bucket(models, model).Add(totals);
                foreach (var (kind, totals) in day.ByKind)
                    Bucket(kinds, kind).Add(totals);
            }

            LlmUsageThread? thread = null;
            if (!string.IsNullOrEmpty(threadId) && _file.Threads.TryGetValue(threadId, out var t))
                thread = new LlmUsageThread { FirstAt = t.FirstAt, LastAt = t.LastAt, Totals = t.Totals.Clone() };

            return new LlmUsageReport(
                Zone.Id,
                _file.Since,
                today,
                _file.Days.TryGetValue(today, out var td) ? td.Totals.Clone() : new LlmUsageTotals(),
                last7,
                last30,
                days,
                [.. models.OrderByDescending(m => m.Value.CostUsd).ThenByDescending(m => m.Value.Requests).Select(m => (m.Key, m.Value))],
                [.. kinds.OrderByDescending(k => k.Value.CostUsd).ThenByDescending(k => k.Value.Requests).Select(k => (k.Key, k.Value))],
                threadId,
                thread);
        }
    }

    /// <summary>Writes pending usage to the file, after pruning past the retention. Safe to call any time.</summary>
    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
                return;
            Prune();
            json = JsonSerializer.Serialize(_file, JsonOptions);
            _dirty = false;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Write then rename, so a reader or a crash never sees half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the LLM usage ledger to {Path}", _path);
            lock (_lock) _dirty = true;
        }
    }

    private void Prune()
    {
        var oldest = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now(), Zone).Date).AddDays(-(_options.RetentionDays - 1));
        foreach (var key in _file.Days.Keys.ToList())
        {
            if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < oldest)
                _file.Days.Remove(key);
        }

        var cutoff = _now().AddDays(-_options.RetentionDays);
        foreach (var (id, _) in _file.Threads.Where(t => t.Value.LastAt < cutoff).ToList())
            _file.Threads.Remove(id);
        if (_file.Threads.Count > _options.MaxThreads)
        {
            foreach (var (id, _) in _file.Threads.OrderBy(t => t.Value.LastAt).Take(_file.Threads.Count - _options.MaxThreads).ToList())
                _file.Threads.Remove(id);
        }
    }

    private LlmUsageLedgerFile Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new LlmUsageLedgerFile();

            var file = JsonSerializer.Deserialize<LlmUsageLedgerFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null)
                return new LlmUsageLedgerFile();

            // Restore the case-insensitive model keys the deserializer doesn't carry over.
            foreach (var day in file.Days.Values)
                day.ByModel = new Dictionary<string, LlmUsageTotals>(day.ByModel, StringComparer.OrdinalIgnoreCase);
            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Usage is an estimate for display; a bad file is set aside rather than failing startup.
            _logger.LogWarning(ex, "Could not read the LLM usage ledger at {Path}; starting a new one", _path);
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
            return new LlmUsageLedgerFile();
        }
    }

    private string DayKey(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LlmUsageTotals Bucket(Dictionary<string, LlmUsageTotals> map, string key)
    {
        if (!map.TryGetValue(key, out var totals))
            map[key] = totals = new LlmUsageTotals();
        return totals;
    }

    private static string ResolvePath(string path, string basePath) =>
        Path.IsPathRooted(path) ? path : Path.Combine(basePath, path);

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
    }
}

/// <summary>Flushes <see cref="LlmUsageLedger"/> when the host stops, so a clean shutdown loses nothing.</summary>
internal sealed class LlmUsageLedgerFlusher(LlmUsageLedger ledger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ledger.Flush();
        return Task.CompletedTask;
    }
}
