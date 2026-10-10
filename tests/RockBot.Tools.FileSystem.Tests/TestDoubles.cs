using Microsoft.Extensions.Logging;

namespace RockBot.Tools.FileSystem.Tests;

/// <summary>Collects formatted log messages so tests can assert on them.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IEnumerable<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
            _entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}
