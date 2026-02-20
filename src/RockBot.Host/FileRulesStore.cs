using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-backed implementation of <see cref="IRulesStore"/>.
/// Loads rules from <c>rules.md</c> in the agent profile directory at startup
/// and persists changes immediately after each add or remove.
/// Thread-safe via an async semaphore.
/// </summary>
internal sealed class FileRulesStore : IRulesStore
{
    private readonly string _filePath;
    private readonly ILogger<FileRulesStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<string> _rules;

    public FileRulesStore(IOptions<AgentProfileOptions> options, ILogger<FileRulesStore> logger)
    {
        _logger = logger;

        var opts = options.Value;
        var baseDir = Path.IsPathRooted(opts.BasePath)
            ? opts.BasePath
            : Path.Combine(AppContext.BaseDirectory, opts.BasePath);

        _filePath = Path.Combine(baseDir, "rules.md");
        _rules = Load();

        _logger.LogInformation("Rules store initialised — {Count} rule(s) loaded from {Path}",
            _rules.Count, _filePath);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Rules => _rules;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_rules);

    /// <inheritdoc />
    public async Task AddAsync(string rule)
    {
        await _lock.WaitAsync();
        try
        {
            if (_rules.Any(r => r.Equals(rule, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("AddRule: rule already exists, skipping — {Rule}", rule);
                return;
            }

            _rules.Add(rule);
            await PersistAsync();
            _logger.LogInformation("Added rule: {Rule}", rule);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string rule)
    {
        await _lock.WaitAsync();
        try
        {
            var removed = _rules.RemoveAll(r => r.Equals(rule, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await PersistAsync();
                _logger.LogInformation("Removed rule: {Rule}", rule);
            }
            else
            {
                _logger.LogDebug("RemoveRule: rule not found — {Rule}", rule);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<string> Load()
    {
        if (!File.Exists(_filePath))
            return [];

        return File.ReadAllLines(_filePath)
            .Select(l => l.TrimStart('-', '*', ' ').Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToList();
    }

    private async Task PersistAsync()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        var lines = new List<string> { "# Active Rules", string.Empty };
        lines.AddRange(_rules.Select(r => $"- {r}"));

        await File.WriteAllLinesAsync(_filePath, lines);
    }
}
