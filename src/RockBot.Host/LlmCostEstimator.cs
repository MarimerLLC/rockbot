using System.Text.Json;
using System.Text.Json.Serialization;
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
        // Azure OpenAI / OpenAI gpt-5.4 family — currently deployed via Azure
        new("gpt-5.4-pro",        30.00, 180.00),
        new("gpt-5.4-mini",        0.75,   4.50),
        new("gpt-5.4",             2.50,  15.00),

        // Claude (Anthropic / OpenRouter)
        new("claude-opus-4",      15.00,  75.00),
        new("claude-sonnet-4",     3.00,  15.00),
        new("claude-haiku-4",      0.80,   4.00),
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

    /// <summary>Estimates cost in USD. Returns 0 when no prefix matches.</summary>
    public double EstimateCost(string modelId, long inputTokens, long outputTokens)
    {
        var table = _table;
        foreach (var entry in table)
        {
            if (modelId.Contains(entry.Prefix, StringComparison.OrdinalIgnoreCase))
                return (inputTokens * entry.InputPerM + outputTokens * entry.OutputPerM) / 1_000_000.0;
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

/// <summary>Single pricing entry: a model ID prefix and per-million-token rates in USD.</summary>
public sealed record LlmPricingEntry(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("inputPerM")] double InputPerM,
    [property: JsonPropertyName("outputPerM")] double OutputPerM);
