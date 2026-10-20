namespace RockBot.Host;

/// <summary>
/// Where the full originals of overflow-trimmed tool results live in working memory, and which
/// of those prefixes belongs to the run now executing.
/// </summary>
/// <remarks>
/// <para>
/// Stash keys follow the <b>session id the run was started with</b>, never a working-memory
/// namespace. The two are different strings — a user session's tools write under
/// <c>session/{id}</c> while its run stashes under <c>stash/{id}</c>, a subagent's tools write
/// under <c>subagent/{taskId}</c> while its run stashes under <c>stash/subagent-{taskId}</c> — so
/// deriving a stash prefix from the tools' namespace never matches anything (#511, #532).
/// </para>
/// <para>
/// The loop binds the run's prefix with <see cref="EnterRun"/> before any tool executes, so a tool
/// asked for "the stash" reads <see cref="CurrentPrefix"/> and gets exactly the prefix the trimmer
/// writes under, whatever namespace it was constructed with.
/// </para>
/// </remarks>
public static class ToolResultStash
{
    /// <summary>Working-memory tag on every stashed original.</summary>
    public const string Tag = "stash";

    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The stash prefix for <paramref name="sessionId"/>; <c>_</c> stands in for no session.</summary>
    public static string KeyPrefix(string? sessionId) =>
        $"stash/{(string.IsNullOrEmpty(sessionId) ? "_" : sessionId)}";

    /// <summary>The working-memory key a run's trimmed result for <paramref name="callId"/> is stashed under.</summary>
    public static string Key(string? sessionId, string callId) => $"{KeyPrefix(sessionId)}/{callId}";

    /// <summary>The tag naming the tool a stashed original came from.</summary>
    public static string ToolTag(string toolName) => $"tool:{toolName}";

    /// <summary>The stash prefix of the run on this async flow, or null outside a run.</summary>
    public static string? CurrentPrefix => Current.Value;

    /// <summary>
    /// Binds the stash prefix for <paramref name="sessionId"/> to the current async flow. Returns a
    /// disposable that restores the previous value, so a nested run hands the outer one back.
    /// </summary>
    public static IDisposable EnterRun(string? sessionId)
    {
        var previous = Current.Value;
        Current.Value = KeyPrefix(sessionId);
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
