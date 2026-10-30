using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Tool families the primary agent rarely needs (#670). On interactive turns they are left out of
/// the tool list until the session asks for them: each family is named after its tool guide, and
/// <c>get_tool_guide(name)</c> for that guide — or a call to one of its tools by name — puts the
/// family's tools in the list for the rest of the conversation.
/// </summary>
public static class DeferredToolFamilies
{
    /// <summary>The tool that loads a family: called with the family's name.</summary>
    public const string LoaderToolName = "get_tool_guide";

    /// <summary>Family (tool-guide name) → the tools it holds, in a fixed order.</summary>
    public static readonly IReadOnlyList<(string Family, string[] Tools)> All =
    [
        ("a2a", ["invoke_agent", "list_known_agents", "get_agent_details", "register_agent", "unregister_agent", "refresh_agent_card"]),
        // set_task_time_zone is registered only on agents that follow the user's location (#740).
        ("scheduling", ["schedule_task", "cancel_scheduled_task", "list_scheduled_tasks", "set_task_time_zone"]),
        ("wisp", ["spawn_wisps"]),
        ("worker", ["spawn_workers"]),
        ("skills", ["save_skill", "edit_skill", "delete_skill"]),
        ("memory", ["edit_memory", "delete_memory", "update_memory_importance"]),
        ("mcp", ["mcp_register_server", "mcp_unregister_server", "mcp_get_prompt", "mcp_list_resources", "mcp_read_resource"]),
    ];

    private static readonly Dictionary<string, string> FamilyByTool = All
        .SelectMany(f => f.Tools.Select(t => (Tool: t, f.Family)))
        .ToDictionary(x => x.Tool, x => x.Family, StringComparer.Ordinal);

    private static readonly HashSet<string> Families = All.Select(f => f.Family).ToHashSet(StringComparer.Ordinal);

    /// <summary>The family <paramref name="toolName"/> belongs to, or null when it is always offered.</summary>
    public static string? FamilyOf(string toolName) => FamilyByTool.GetValueOrDefault(toolName);

    /// <summary>True when <paramref name="name"/> names a deferred family.</summary>
    public static bool IsFamily(string? name) => name is not null && Families.Contains(name);
}

/// <summary>
/// Which deferred tool families each session has loaded (#670), oldest first. A family stays loaded
/// for the conversation — until the session goes idle for <see cref="IdleTimeout"/> — and new ones
/// are appended, so a session's tool list only ever grows at the end and the provider's prompt
/// cache prefix holds.
/// </summary>
public sealed class DeferredToolActivations(TimeProvider? timeProvider = null)
{
    /// <summary>Process-wide store for runners constructed outside dependency injection.</summary>
    public static DeferredToolActivations Shared { get; } = new();

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>How long a session keeps its loaded families without any run touching them.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>The families <paramref name="sessionId"/> has loaded, oldest first.</summary>
    public IReadOnlyList<string> Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return [];
        lock (session)
        {
            if (_time.GetUtcNow() - session.LastTouched > IdleTimeout)
            {
                _sessions.TryRemove(sessionId, out _);
                return [];
            }
            session.LastTouched = _time.GetUtcNow();
            return [.. session.Families];
        }
    }

    /// <summary>Loads <paramref name="family"/> for <paramref name="sessionId"/>. Returns false when it already was.</summary>
    public bool Activate(string sessionId, string family)
    {
        var session = _sessions.GetOrAdd(sessionId, _ => new Session(_time.GetUtcNow()));
        lock (session)
        {
            if (_time.GetUtcNow() - session.LastTouched > IdleTimeout)
                session.Families.Clear();
            session.LastTouched = _time.GetUtcNow();
            if (session.Families.Contains(family))
                return false;
            session.Families.Add(family);
            return true;
        }
    }

    private sealed class Session(DateTimeOffset now)
    {
        public List<string> Families { get; } = [];
        public DateTimeOffset LastTouched { get; set; } = now;
    }
}

/// <summary>
/// The current run's deferred tools (#670): the ones <see cref="Partition"/> took out of its list,
/// keyed by name, and the session whose activations decide which come back. Ambient per agent loop
/// run (set by <see cref="AgentLoopRunner.RunAsync"/>), like <see cref="TypedToolSurfaceContext"/>,
/// so the chat-client pipeline can add a family the moment the model asks for it.
/// </summary>
public static class DeferredToolContext
{
    private static readonly AsyncLocal<Run?> Current = new();

    /// <summary>The first line of the system message that lists the deferred families.</summary>
    public const string OrientationHeading = "## Tools available on request";

    /// <summary>
    /// Binds deferral for a run. Pass a null <paramref name="store"/> to bind "no deferral", so a
    /// nested run never inherits its parent's.
    /// </summary>
    public static IDisposable Set(DeferredToolActivations? store, string? sessionId, ILogger? logger = null)
    {
        var previous = Current.Value;
        Current.Value = store is null || string.IsNullOrEmpty(sessionId) ? null : new Run(store, sessionId, logger);
        return new Scope(previous);
    }

    /// <summary>True when the current run defers tools.</summary>
    public static bool IsActive => Current.Value is not null;

    /// <summary>
    /// Moves the deferred families' tools out of <paramref name="options"/> into the run's pool, then
    /// puts back the families the session has already loaded. Tools the caller scoped to the run
    /// (<see cref="ICallerScopedTool"/>) are never deferred. Returns how many tools stay out.
    /// </summary>
    public static int Partition(ChatOptions? options)
    {
        // Without the loader in the list a held-back family could never come back, so defer nothing.
        if (Current.Value is not { } run || !HasLoader(options) || options.Tools is not { Count: > 0 } tools)
            return 0;

        var kept = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (DeferredToolFamilies.FamilyOf(tool.Name) is not null && tool.GetService<ICallerScopedTool>() is null)
                run.Pool[tool.Name] = tool;
            else
                kept.Add(tool);
        }
        if (run.Pool.Count == 0)
            return 0;

        options.Tools = kept;
        AddActivated(options);
        var present = options.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return run.Pool.Keys.Count(name => !present.Contains(name));
    }

    /// <summary>Appends the tools of every family the session has loaded that <paramref name="options"/> doesn't hold yet.</summary>
    public static int AddActivated(ChatOptions? options)
    {
        if (Current.Value is not { } run || run.Pool.Count == 0 || !HasLoader(options))
            return 0;

        var added = 0;
        foreach (var family in run.Store.Get(run.SessionId))
            added += AppendFamily(run, options, family);
        return added;
    }

    /// <summary>
    /// Reacts to a tool call the model made: <c>get_tool_guide</c> for a deferred family, or a call
    /// to a deferred tool the list doesn't hold, loads that family for the session and adds its
    /// tools to <paramref name="options"/>. Returns the tools added.
    /// </summary>
    /// <param name="guideName">The <c>name</c> argument of a <c>get_tool_guide</c> call; ignored for other tools.</param>
    public static int Observe(ChatOptions? options, string toolName, string? guideName)
    {
        if (Current.Value is not { } run || run.Pool.Count == 0 || !HasLoader(options))
            return 0;

        var family = toolName == DeferredToolFamilies.LoaderToolName
            ? (DeferredToolFamilies.IsFamily(guideName) ? guideName : null)
            : (run.Pool.ContainsKey(toolName) ? DeferredToolFamilies.FamilyOf(toolName) : null);
        if (family is null)
            return 0;

        if (run.Store.Activate(run.SessionId, family))
        {
            run.Logger?.LogInformation(
                "Deferred tool family '{Family}' loaded for session {SessionId} (via {Tool})",
                family, run.SessionId, toolName);
        }
        return AppendFamily(run, options, family);
    }

    /// <summary>
    /// Resolves a call to <paramref name="toolName"/>, which isn't in <paramref name="options"/>, as
    /// a deferred tool: loads its family and returns the tool, or null.
    /// </summary>
    public static AIFunction? TryActivate(ChatOptions? options, string toolName)
    {
        if (Current.Value is not { } run || !run.Pool.ContainsKey(toolName))
            return null;
        Observe(options, toolName, guideName: null);
        return options?.Tools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolName);
    }

    /// <summary>
    /// The system message naming the families held back from this run and how to load them, or null
    /// when nothing is deferred. Lists every family in the run's pool whether loaded or not, so the
    /// text is the same on every turn and stays in the cached prompt prefix.
    /// </summary>
    public static string? Orientation()
    {
        if (Current.Value is not { } run || run.Pool.Count == 0)
            return null;

        var sb = new StringBuilder(OrientationHeading).AppendLine();
        sb.AppendLine(
            "These tools are not in your tool list until you load them. To use one, call " +
            $"{DeferredToolFamilies.LoaderToolName}(name=\"<family>\"): that returns the family's guide and adds " +
            "its tools for the rest of this conversation. Calling one of the tools by name also loads its family.");
        foreach (var (family, names) in DeferredToolFamilies.All)
        {
            var present = names.Where(run.Pool.ContainsKey).ToList();
            if (present.Count > 0)
                sb.Append("- ").Append(family).Append(": ").AppendLine(string.Join(", ", present));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The <c>name</c> argument of a <c>get_tool_guide</c> call from its JSON arguments, or null.</summary>
    public static string? GuideNameFromJson(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("name", out var name)
                   && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>name</c> argument of a <c>get_tool_guide</c> call from its parsed arguments, or null.</summary>
    public static string? GuideName(IDictionary<string, object?>? arguments) =>
        arguments is not null && arguments.TryGetValue("name", out var value) ? value?.ToString() : null;

    // Families join only a list that holds the loader — the run's own tool list — never an
    // auxiliary request inside the run (the completion evaluator's, say) that carries other tools.
    private static bool HasLoader([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ChatOptions? options) =>
        options?.Tools?.Any(t => t.Name == DeferredToolFamilies.LoaderToolName) == true;

    private static int AppendFamily(Run run, ChatOptions options, string family)
    {
        var present = (options.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var toAdd = DeferredToolFamilies.All
            .Where(f => f.Family == family)
            .SelectMany(f => f.Tools)
            .Where(name => run.Pool.ContainsKey(name) && !present.Contains(name))
            .Select(name => run.Pool[name])
            .ToList();
        if (toAdd.Count == 0)
            return 0;

        if (options.Tools is null || options.Tools.IsReadOnly)
            options.Tools = [.. options.Tools ?? [], .. toAdd];
        else
            foreach (var tool in toAdd)
                options.Tools.Add(tool);
        return toAdd.Count;
    }

    private sealed class Run(DeferredToolActivations store, string sessionId, ILogger? logger)
    {
        public DeferredToolActivations Store { get; } = store;
        public string SessionId { get; } = sessionId;
        public ILogger? Logger { get; } = logger;
        public Dictionary<string, AITool> Pool { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Scope(Run? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
