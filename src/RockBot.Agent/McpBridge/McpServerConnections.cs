using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol.Client;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// The bridge's per-server state: which servers are configured, and an immutable
/// <see cref="ConnectedServer"/> snapshot of each connected one.
/// <para>
/// Readers take one snapshot and use it for the whole operation, so the client, tools, config
/// and elicitation coordinator they see always come from the same connect — never a new client
/// with an old tool list. Writers (connect, refresh, disconnect) hold the server's lock from
/// <see cref="LockAsync"/>, so at most one runs per server at a time, and swap the snapshot with
/// <see cref="Publish"/>. A replaced connection is retired: its client is disposed once the last
/// call holding a <see cref="ServerLease"/> on it finishes, never under a running call.
/// </para>
/// See issue #604.
/// </summary>
internal sealed class McpServerConnections
{
    private readonly ConcurrentDictionary<string, McpBridgeServerConfig> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectedServer> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names of every configured server, connected or not. A configured server that isn't
    /// connected is what the reconnect sweep retries.
    /// </summary>
    public IReadOnlyCollection<string> ConfiguredNames => [.. _configured.Keys];

    /// <summary>The latest configuration for <paramref name="name"/>, whether or not it is connected.</summary>
    public bool TryGetConfig(string name, [MaybeNullWhen(false)] out McpBridgeServerConfig config) =>
        _configured.TryGetValue(name, out config);

    public bool IsConfigured(string name) => _configured.ContainsKey(name);

    /// <summary>Records <paramref name="config"/> as the latest configuration for <paramref name="name"/>.</summary>
    public void SetConfig(string name, McpBridgeServerConfig config) => _configured[name] = config;

    /// <summary>Snapshots of every connected server.</summary>
    public IReadOnlyList<ConnectedServer> Connected => [.. _connected.Values];

    public bool IsConnected(string name) => _connected.ContainsKey(name);

    /// <summary>
    /// The current snapshot for <paramref name="name"/>, for reading. To <i>use</i> its client,
    /// take a <see cref="Lease"/> instead, so the client can't be disposed mid-call.
    /// </summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out ConnectedServer server) =>
        _connected.TryGetValue(name, out server);

    /// <summary>
    /// Leases the current snapshot for the duration of a call. The snapshot's client stays
    /// undisposed until the lease is disposed, even if the server reconnects meanwhile. Returns
    /// null when the server isn't connected.
    /// </summary>
    public ServerLease? Lease(string name)
    {
        // A snapshot can be retired between the read and the acquire; the map already holds its
        // replacement (or nothing) by then, so the retry sees current state and the loop ends.
        while (_connected.TryGetValue(name, out var server))
        {
            if (server.Resources.TryAcquire())
                return new ServerLease(server);
        }
        return null;
    }

    /// <summary>
    /// Serialises connect, refresh and disconnect for one server. Dispose the result to release.
    /// Not reentrant: code holding the lock must not call anything that takes it again.
    /// </summary>
    public async Task<IDisposable> LockAsync(string name, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <summary>
    /// Makes <paramref name="next"/> the server's current snapshot. When it replaces a snapshot
    /// with a different connection, that connection is retired. The caller must hold the
    /// server's <see cref="LockAsync"/>.
    /// </summary>
    public void Publish(ConnectedServer next)
    {
        _connected.TryGetValue(next.Name, out var previous);
        _connected[next.Name] = next;
        if (previous is not null && !ReferenceEquals(previous.Resources, next.Resources))
            previous.Resources.Retire();
    }

    /// <summary>
    /// Drops the server's snapshot, retiring its connection, and — when
    /// <paramref name="forgetConfig"/> — its configuration too. The caller must hold the server's
    /// <see cref="LockAsync"/>. Returns the removed snapshot, if there was one.
    /// </summary>
    public ConnectedServer? Remove(string name, bool forgetConfig)
    {
        if (forgetConfig)
            _configured.TryRemove(name, out _);

        if (!_connected.TryRemove(name, out var removed))
            return null;

        removed.Resources.Retire();
        return removed;
    }

    /// <summary>
    /// Retires every connection (shutdown) and waits up to <paramref name="grace"/> for calls
    /// still holding leases to finish and the clients to be disposed.
    /// </summary>
    public async Task RetireAllAsync(TimeSpan grace)
    {
        var retiring = _connected.Values.ToList();
        _connected.Clear();
        foreach (var server in retiring)
            server.Resources.Retire();

        await Task.WhenAny(Task.WhenAll(retiring.Select(s => s.Resources.Disposed)), Task.Delay(grace));
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}

/// <summary>
/// Everything the bridge knows about one connected server, all from the same connect (or the
/// same surface refresh on that connection). Immutable: a change publishes a new snapshot.
/// </summary>
internal sealed record ConnectedServer
{
    public required string Name { get; init; }

    /// <summary>The configuration this snapshot applies — tool filters, guards, timeouts.</summary>
    public required McpBridgeServerConfig Config { get; init; }

    public required McpClient Client { get; init; }

    /// <summary>Tools the server lists, after <see cref="Config"/>'s allow/deny filters.</summary>
    public required IReadOnlyList<McpClientTool> Tools { get; init; }

    public required IReadOnlyList<McpClientPrompt> Prompts { get; init; }

    /// <summary>
    /// MCP resources the server lists (#617); empty for a server without the capability. Not to
    /// be confused with <see cref="Resources"/>, the connection's disposables.
    /// </summary>
    public IReadOnlyList<McpClientResource> DownstreamResources { get; init; } = [];

    /// <summary>Resource templates the server lists (#617); empty for a server without the capability.</summary>
    public IReadOnlyList<McpClientResourceTemplate> DownstreamResourceTemplates { get; init; } = [];

    public required McpServerMetadata Metadata { get; init; }

    public required McpServerSummary Summary { get; init; }

    /// <summary>Elicitation policy for this connection; null when the server's policy is off.</summary>
    public McpElicitationCoordinator? Elicitation { get; init; }

    /// <summary>
    /// The connection's attachment-passthrough gateway, built on first use; null when the server
    /// has no attachment manifest. Its HTTP client is owned by <see cref="Resources"/>.
    /// </summary>
    public required Lazy<AttachmentGateway?> AttachmentGateway { get; init; }

    /// <summary>
    /// Owns the client and anything else created for this connection, and disposes them once the
    /// connection is retired and no call holds a lease. Shared by snapshots of the same
    /// connection (a surface refresh republishes with the same resources).
    /// </summary>
    public required ConnectionResources Resources { get; init; }
}

/// <summary>
/// Snapshot of a connected MCP server's self-reported identity, captured once at connect from
/// <see cref="McpClient.ServerInfo"/> and <see cref="McpClient.ServerInstructions"/>.
/// </summary>
internal sealed record McpServerMetadata(
    string? ImplementationName,
    string? Title,
    string? Version,
    string? Description,
    string? Instructions);

/// <summary>
/// A call's hold on a <see cref="ConnectedServer"/> snapshot: its connection isn't disposed until
/// every lease on it is disposed.
/// </summary>
internal sealed class ServerLease(ConnectedServer server) : IDisposable
{
    private int _released;

    public ConnectedServer Server { get; } = server;

    /// <summary>
    /// Another lease on <paramref name="server"/>'s connection, for work that outlives the lease
    /// the caller holds (a parked call). Only valid while the caller still holds one, so the
    /// connection can't have been disposed; unlike <see cref="McpServerConnections.Lease"/> it
    /// succeeds even when the connection has since been retired.
    /// </summary>
    public static ServerLease Share(ConnectedServer server)
    {
        server.Resources.AddLease();
        return new ServerLease(server);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            Server.Resources.Release();
    }
}

/// <summary>
/// Disposables owned by one connection, with lease counting: <see cref="Retire"/> marks the
/// connection replaced or removed, and the owned objects are disposed when it is retired and the
/// last lease is released — whichever happens second.
/// </summary>
internal sealed class ConnectionResources
{
    private readonly object _gate = new();
    private readonly List<object> _owned = [];
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _leases;
    private bool _retired;
    private bool _disposing;

    /// <param name="owned">Objects to dispose, each <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.</param>
    public ConnectionResources(params object[] owned) => _owned.AddRange(owned);

    /// <summary>Completes once the owned objects have been disposed.</summary>
    public Task Disposed => _disposed.Task;

    public bool IsRetired { get { lock (_gate) return _retired; } }

    public int ActiveLeases { get { lock (_gate) return _leases; } }

    /// <summary>
    /// Takes ownership of another object created for this connection. Disposed at once when the
    /// connection has already been disposed.
    /// </summary>
    public void Add(object disposable)
    {
        lock (_gate)
        {
            if (!_disposing)
            {
                _owned.Add(disposable);
                return;
            }
        }

        _ = DisposeOneAsync(disposable);
    }

    /// <summary>Takes a lease, unless the connection has been retired.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_retired) return false;
            _leases++;
            return true;
        }
    }

    /// <summary>
    /// Takes a lease whether or not the connection is retired. The caller must already hold one,
    /// which guarantees the owned objects haven't been disposed.
    /// </summary>
    public void AddLease()
    {
        lock (_gate) _leases++;
    }

    public void Release()
    {
        bool dispose;
        lock (_gate)
        {
            _leases--;
            dispose = StartDisposingIfDone();
        }

        if (dispose)
            _ = DisposeOwnedAsync();
    }

    /// <summary>Marks the connection replaced or removed. Idempotent.</summary>
    public void Retire()
    {
        bool dispose;
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            dispose = StartDisposingIfDone();
        }

        if (dispose)
            _ = DisposeOwnedAsync();
    }

    private bool StartDisposingIfDone()
    {
        if (!_retired || _leases > 0 || _disposing) return false;
        _disposing = true;
        return true;
    }

    private async Task DisposeOwnedAsync()
    {
        List<object> owned;
        lock (_gate) owned = [.. _owned];

        foreach (var item in owned)
            await DisposeOneAsync(item);

        _disposed.TrySetResult();
    }

    private static async Task DisposeOneAsync(object item)
    {
        try
        {
            switch (item)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch
        {
            // Best-effort cleanup: a failing dispose must not block the others.
        }
    }
}
