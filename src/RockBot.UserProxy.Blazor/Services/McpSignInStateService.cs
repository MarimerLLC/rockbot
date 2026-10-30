using System.Collections.Concurrent;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Which OAuth-protected MCP servers need the user to sign in (#738), as the agent last reported.
/// A singleton fed by <see cref="BlazorUserFrontend"/>, so every open page shows the same prompts;
/// components bind to <see cref="StateChanged"/> to re-render.
/// </summary>
public sealed class McpSignInStateService
{
    private readonly ConcurrentDictionary<string, Entry> _servers = new(StringComparer.OrdinalIgnoreCase);
    private int _seeded;

    /// <summary>Fires when any server's sign-in state changes.</summary>
    public event Action? StateChanged;

    /// <summary>One server's prompt: why it needs a sign-in, and whether the last attempt failed.</summary>
    public sealed record Entry(string ServerName, string State, string? Message);

    /// <summary>Servers that need the user to sign in, or whose last sign-in failed.</summary>
    public IReadOnlyList<Entry> NeedingSignIn =>
        [.. _servers.Values.OrderBy(e => e.ServerName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// True for the first caller only, which should seed the state from the agent's list; the
    /// agent's broadcasts keep it current after that.
    /// </summary>
    public bool TryClaimSeed() => Interlocked.Exchange(ref _seeded, 1) == 0;

    /// <summary>Lets a later page try to seed again, after the agent didn't answer.</summary>
    public void ReleaseSeed() => Interlocked.Exchange(ref _seeded, 0);

    /// <summary>Seeds the state from the agent's list of OAuth servers.</summary>
    public void Seed(McpSignInListResponse list)
    {
        foreach (var server in list.Servers)
        {
            if (server.State == McpSignInState.Required)
                _servers.TryAdd(server.ServerName, new Entry(server.ServerName, McpSignInState.Required, null));
        }
        StateChanged?.Invoke();
    }

    /// <summary>Applies a status the agent broadcast.</summary>
    public void Apply(McpSignInStatus status)
    {
        switch (status.State)
        {
            case McpSignInState.Required:
            case McpSignInState.Failed:
            case McpSignInState.Expired:
                _servers[status.ServerName] = new Entry(status.ServerName, status.State, status.Message);
                break;
            case McpSignInState.Completed:
            case McpSignInState.SignedIn:
                _servers.TryRemove(status.ServerName, out _);
                break;
            default:
                // Pending and cancelled leave the prompt as it was: a newer sign-in is under way.
                return;
        }
        StateChanged?.Invoke();
    }
}
