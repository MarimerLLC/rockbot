using System.Collections.Concurrent;

namespace RockBot.Tools;

/// <summary>
/// Thread-safe in-memory tool registry.
/// </summary>
internal sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

    public IReadOnlyList<ToolRegistration> GetTools() =>
        _tools.Values.Select(t => t.Registration).ToList();

    public IToolExecutor? GetExecutor(string toolName) =>
        _tools.TryGetValue(toolName, out var entry) ? entry.Executor : null;

    public void Register(ToolRegistration registration, IToolExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(executor);

        if (!_tools.TryAdd(registration.Name, (registration, executor)))
            throw new InvalidOperationException($"Tool '{registration.Name}' is already registered.");
    }

    public bool Unregister(string toolName) =>
        _tools.TryRemove(toolName, out _);
}
