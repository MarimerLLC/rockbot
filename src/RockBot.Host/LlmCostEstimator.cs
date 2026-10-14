using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Estimates LLM call cost in USD from token counts and model ID.
/// Pricing is loaded from a JSON file on the agent PVC (see <see cref="LlmPricingOptions"/>),
/// hot-reloaded on file change. When the file is missing or invalid, a small built-in
/// fallback table is used so cost metrics keep flowing rather than collapsing to zero.
/// </summary>
public sealed class LlmCostEstimator : IDisposable
{
    private static readonly LlmPricingEntry[] BuiltInDefaults =
    [
        // Azure OpenAI / OpenAI gpt-5.x family — currently deployed via Azure.
        // gpt-5.6-sol is at list price; a $4/$20 promo runs through at least 2026-11-30.
        new("gpt-5.6-sol",         5.00,  30.00),
        new("gpt-5.6-terra",       2.00,  12.00),
        new("gpt-5.6-luna",        0.20,   1.20),
        new("gpt-5.5-pro",        30.00, 180.00),
        new("gpt-5.5",             5.00,  30.00),
        new("gpt-5.4-pro",        30.00, 180.00),
        new("gpt-5.4-mini",        0.75,   4.50),
        new("gpt-5.4",             2.50,  15.00),

        // Claude (Anthropic / OpenRouter). Cache reads are priced per model; cache writes
        // fall back to 1.25x input (5-minute TTL) and 2x input (1-hour TTL).
        new("claude-fable-5-1",   10.00,  50.00, CachedInputPerM: 0.25),
        new("claude-fable-5",     10.00,  50.00, CachedInputPerM: 1.00),
        new("claude-opus-5-5",     4.00,  20.00, CachedInputPerM: 0.20),
        new("claude-opus-5",       5.00,  25.00, CachedInputPerM: 0.50),
        new("claude-sonnet-5",     2.00,  10.00, CachedInputPerM: 0.20),
        // Haiku 5.5 rises to $0.50/$2.50 above 100K prompt tokens; the base rate is used here.
        new("claude-haiku-5-5",    0.10,   0.50, CachedInputPerM: 0.01),
        new("claude-opus-4",      15.00,  75.00, CachedInputPerM: 1.50),
        new("claude-sonnet-4",     3.00,  15.00, CachedInputPerM: 0.30),
        new("claude-haiku-4",      0.80,   4.00, CachedInputPerM: 0.08),
        new("claude-3-opus",      15.00,  75.00),
        new("claude-3-5-sonnet",   3.00,  15.00),
        new("claude-3-5-haiku",    0.80,   4.00),
        new("claude-3-haiku",      0.25,   1.25),

        // OpenAI legacy
        new("gpt-5.3",             1.75,  14.00),
        new("gpt-4o-mini",         0.15,   0.60),
        new("gpt-4o",              2.50,  10.00),
        new("gpt-4-turbo",        10.00,  30.00),
        new("o1-mini",             1.10,   4.40),
        new("o1",                 15.00,  60.00),
        new("o3-mini",             1.10,   4.40),

        // Google
        new("gemini-3.1-pro",      2.00,  12.00),
        new("gemini-3-flash",      0.50,   3.00),
        new("gemini-2.5-pro",      1.25,  10.00),
        new("gemini-2.5-flash",    0.15,   0.60),
        new("gemini-2.0-flash",    0.10,   0.40),
        new("gemini-1.5-pro",      1.25,   5.00),
        new("gemini-1.5-flash",    0.075,  0.30),

        // DeepSeek
        new("deepseek-chat",       0.14,   0.28),
        new("deepseek-r1",         0.55,   2.19),
    ];

    private readonly LlmPricingOptions _options;
    private readonly ILogger<LlmCostEstimator> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private int _reloadPending;

    // Sorted longest-prefix-first so "gpt-5.4-pro" wins over "gpt-5.4".
    private LlmPricingEntry[] _table = SortLongestFirst(BuiltInDefaults);

    public LlmCostEstimator(IOptions<LlmPricingOptions> options, ILogger<LlmCostEstimator> logger)
    {
        _options = options.Value;
        _logger = logger;
        Reload();
        StartWatching();
    }

    /// <summary>Estimates cost in USD, all input at the full rate. Returns 0 when no prefix matches.</summary>
    public double EstimateCost(string modelId, long inputTokens, long outputTokens)
        => EstimateCost(modelId, inputTokens, outputTokens, 0, 0, 0);

    /// <summary>Estimates cost in USD from a response's usage, pricing cache reads and writes.</summary>
    public double EstimateCost(string modelId, UsageDetails usage)
        => EstimateCost(modelId,
            usage.InputTokenCount ?? 0,
            usage.OutputTokenCount ?? 0,
            UsageReader.GetCachedInputTokens(usage),
            UsageReader.GetCacheWriteTokens(usage),
            UsageReader.GetCacheWrite1hTokens(usage));

    /// <summary>
    /// Estimates cost in USD. <paramref name="inputTokens"/> is the total input, including
    /// cache reads and cache writes (both the OpenAI and Anthropic adapters report it that
    /// way). <paramref name="cacheWrite1hTokens"/> is the part of
    /// <paramref name="cacheWriteTokens"/> written with the 1-hour TTL. Returns 0 when no
    /// prefix matches.
    /// </summary>
    public double EstimateCost(string modelId, long inputTokens, long outputTokens,
        long cachedInputTokens, long cacheWriteTokens, long cacheWrite1hTokens)
    {
        var table = _table;
        foreach (var entry in table)
        {
            if (!modelId.Contains(entry.Prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var write1h = Math.Clamp(cacheWrite1hTokens, 0, cacheWriteTokens);
            var write5m = cacheWriteTokens - write1h;
            var uncached = Math.Max(0, inputTokens - cachedInputTokens - cacheWriteTokens);

            return (uncached * entry.InputPerM
                  + cachedInputTokens * (entry.CachedInputPerM ?? entry.InputPerM)
                  + write5m * (entry.CacheWrite5mPerM ?? entry.InputPerM * 1.25)
                  + write1h * (entry.CacheWrite1hPerM ?? entry.InputPerM * 2.0)
                  + outputTokens * entry.OutputPerM) / 1_000_000.0;
        }
        return 0.0;
    }

    private void Reload()
    {
        try
        {
            if (!File.Exists(_options.ConfigPath))
            {
                _logger.LogInformation("LLM pricing file {Path} not found, using built-in defaults", _options.ConfigPath);
                _table = SortLongestFirst(BuiltInDefaults);
                return;
            }

            var json = File.ReadAllText(_options.ConfigPath);
            var entries = JsonSerializer.Deserialize<LlmPricingEntry[]>(json, JsonOpts);
            if (entries is null || entries.Length == 0)
            {
                _logger.LogWarning("LLM pricing file {Path} parsed as empty, using built-in defaults", _options.ConfigPath);
                _table = SortLongestFirst(BuiltInDefaults);
                return;
            }

            _table = SortLongestFirst(entries);
            _logger.LogInformation("Loaded {Count} LLM pricing entries from {Path}", entries.Length, _options.ConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load LLM pricing from {Path}, keeping previous table", _options.ConfigPath);
        }
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_options.ConfigPath);
        var file = Path.GetFileName(_options.ConfigPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger.LogDebug("LLM pricing directory {Dir} does not exist, file watching disabled", dir);
            return;
        }

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                try { Reload(); }
                finally { Interlocked.Exchange(ref _reloadPending, 0); }
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private static LlmPricingEntry[] SortLongestFirst(IEnumerable<LlmPricingEntry> entries)
        => entries.OrderByDescending(e => e.Prefix.Length).ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce?.Dispose();
        _debounce = null;
    }
}

/// <summary>
/// Single pricing entry: a model ID prefix and per-million-token rates in USD. The cache rates
/// are optional: a missing <see cref="CachedInputPerM"/> prices cache reads at the full input
/// rate (no discount assumed), and missing write rates use Anthropic's multipliers, 1.25x input
/// for the 5-minute TTL and 2x for the 1-hour TTL. Only providers that report cache writes
/// (Anthropic) are charged for them.
/// </summary>
public sealed record LlmPricingEntry(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("inputPerM")] double InputPerM,
    [property: JsonPropertyName("outputPerM")] double OutputPerM,
    [property: JsonPropertyName("cachedInputPerM")] double? CachedInputPerM = null,
    [property: JsonPropertyName("cacheWrite5mPerM")] double? CacheWrite5mPerM = null,
    [property: JsonPropertyName("cacheWrite1hPerM")] double? CacheWrite1hPerM = null);
