using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.Watches;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// Resource watches (SEP-2575 <c>subscriptions/listen</c>, protocol 2026-07-28):
/// <c>mcp_watch_resource</c> asks the bridge to tell the calling session when a server resource
/// changes. For each server with watches the bridge holds one listen stream carrying every watched
/// URI. A <c>notifications/resources/updated</c> starts a short debounce. The bridge then reads the
/// resource once and, if its content hash moved, delivers the content to the session as a new turn,
/// by the same path as a background task's result.
/// <para>
/// A subscription is only the open stream, so it is opened again whenever it ends: the server
/// closing it (the scheduler does after 25 minutes, in place of keepalives), an error, the
/// bridge's own maximum age, a new connection or token, or a change in what is watched. After
/// every opening each watched resource is read again, and a change missed while the stream was
/// down (or the agent restarted) is delivered then. The watch ledger keeps what to subscribe to
/// across restarts. See <c>design/mcp-bridge.md</c>, "Resource watches".
/// </para>
/// </summary>
public sealed partial class McpBridgeService
{
    /// <summary>A stream that lived at least this long counts as healthy: it is reopened at once.</summary>
    private static readonly TimeSpan WatchStreamHealthyLife = TimeSpan.FromSeconds(30);

    /// <summary>How often an open stream checks that its connection is still the server's current one.</summary>
    private static readonly TimeSpan WatchConnectionCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>How often watches are checked for their time-to-live.</summary>
    private static readonly TimeSpan WatchExpiryCheckInterval = TimeSpan.FromHours(1);

    /// <summary>Consecutive failed reads of a changed resource before the bridge leaves it to the next reconnect.</summary>
    private const int WatchReadFailureLimit = 5;

    private McpWatchLedger _watchLedger = null!;
    private readonly ConcurrentDictionary<string, ActiveWatch> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WatchStream> _watchStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _watchGate = new(1, 1);

    private void InitializeWatches(TimeProvider? timeProvider)
    {
        var path = Path.IsPathRooted(_options.WatchLedgerPath)
            ? _options.WatchLedgerPath
            : Path.Combine(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory, _options.WatchLedgerPath);
        _watchLedger = new McpWatchLedger(path, _logger, timeProvider);
    }

    /// <summary>Whether a connected server lets the agent watch its resources.</summary>
    private static bool OffersWatches(McpBridgeServerConfig config, McpClient client) =>
        config.WatchesResources
        && IsJuly2026OrLater(client.NegotiatedProtocolVersion)
        && client.ServerCapabilities?.Resources?.Subscribe == true;

    // ── Agent requests ──────────────────────────────────────────────────────

    /// <summary><c>mcp_watch_resource</c>: starts watching a resource for the asking session.</summary>
    private async Task<McpWatchResourceResponse> WatchResourceAsync(McpWatchResourceRequest req, CancellationToken ct)
    {
        var uri = req.Uri?.Trim() ?? "";
        McpWatchResourceResponse Refuse(string error) => new() { ServerName = req.ServerName ?? "", Uri = uri, Error = error };

        if (!McpTaskContext.IsBackgroundCapableSession(req.SessionId))
            return Refuse(McpWatchText.RefusedSession);
        if (uri.Length == 0)
            return Refuse("'uri' is required: the resource URI as the server lists it (see mcp_list_resources), any template expanded.");
        var sessionId = req.SessionId!;

        await _watchGate.WaitAsync(ct);
        try
        {
            if (!_connections.TryGet(req.ServerName ?? "", out var server))
            {
                return Refuse(_connections.IsConfigured(req.ServerName ?? "")
                    ? McpCallDiagnostics.DescribeUnavailableServer(req.ServerName ?? "")
                    : McpCallDiagnostics.DescribeUnknownServer(req.ServerName ?? "", _connections.ConfiguredNames));
            }

            var name = server.Name;
            if (!server.Summary.Subscriptions)
                return Refuse(McpWatchText.NotOffered(name, server.Config.WatchesResources));

            if (FindWatch(sessionId, name, uri) is { } existing)
            {
                return new McpWatchResourceResponse
                {
                    ServerName = name, Uri = uri, WatchId = existing.WatchId,
                    Message = McpWatchText.AlreadyWatching(existing.Entry),
                };
            }

            var mine = SessionWatchEntries(sessionId);
            var perSession = Math.Max(1, _options.MaxWatchesPerSession);
            if (mine.Count >= perSession)
                return Refuse(McpWatchText.CapReached("session", perSession, mine));
            var total = Math.Max(1, _options.MaxWatches);
            if (_watches.Count >= total)
                return Refuse(McpWatchText.CapReached("agent", total, mine));

            // The baseline: what the resource holds now. A resource that can't be read can't be watched.
            string hash;
            using (var lease = _connections.Lease(name))
            {
                if (lease is null)
                    return Refuse(McpCallDiagnostics.DescribeUnavailableServer(name));
                try
                {
                    hash = HashContents(await ReadWatchedAsync(lease.Server, uri, ct));
                }
                catch (McpProtocolException ex)
                {
                    return Refuse(McpWatchText.Unreadable(name, uri, ex.Message));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return Refuse(McpWatchText.Unreadable(name, uri, ex.Message));
                }
            }

            var now = _time.GetUtcNow();
            var entry = new McpWatchEntry
            {
                WatchId = NewWatchId(),
                SessionId = sessionId,
                Server = name,
                ServerId = server.Config.Id,
                Uri = uri,
                LastHash = hash,
                RunOrigin = req.RunOrigin,
                UserAskedFor = req.UserAskedFor,
                CreatedAt = now,
                ExpiresAt = now + TimeSpan.FromDays(Math.Max(1, _options.WatchTtlDays)),
            };

            // Write-ahead: the agent hears of no watch the ledger doesn't hold.
            await _watchLedger.AddAsync(entry, CancellationToken.None);
            var watch = new ActiveWatch(entry, confirmed: false);
            _watches[entry.WatchId] = watch;

            var failure = await SubscribeAsync(name, uri, ct);
            if (failure is not null)
            {
                await EndWatchAsync(watch, $"the server didn't take the subscription ({failure})", notify: false);
                return Refuse(McpWatchText.NotAccepted(name, uri, failure));
            }

            watch.Confirmed = true;
            _logger.LogInformation("Session {Session} now watches {Server} {Uri} ({WatchId})", sessionId, name, uri, entry.WatchId);
            return new McpWatchResourceResponse
            {
                ServerName = name, Uri = uri, WatchId = entry.WatchId, Message = McpWatchText.Watching(entry),
            };
        }
        finally
        {
            _watchGate.Release();
        }
    }

    /// <summary>
    /// Makes sure the server's stream carries <paramref name="uri"/>: reopens it with the URI
    /// added, unless the open stream already has it. Returns why the server didn't take it, or null.
    /// </summary>
    private async Task<string?> SubscribeAsync(string server, string uri, CancellationToken ct)
    {
        var stream = EnsureWatchStream(server);
        if (stream.Granted.Contains(uri))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.SubscriptionAckTimeoutSeconds) * 2));
        try
        {
            var next = stream.NextOpening;
            stream.Recycle();
            var restarted = false;
            while (true)
            {
                var opening = await next.WaitAsync(timeout.Token);
                if (opening.Final)
                {
                    // The stream stopped as its last other watch ended; this watch needs a new one.
                    if (restarted || !_watches.Values.Any(w => string.Equals(w.Entry.Uri, uri, StringComparison.Ordinal)
                                                              && string.Equals(w.Entry.Server, server, StringComparison.OrdinalIgnoreCase)))
                        return opening.Failure;
                    restarted = true;
                    stream = EnsureWatchStream(server);
                    next = stream.NextOpening;
                    stream.Recycle();
                    continue;
                }

                if (opening.Requested.Contains(uri))
                {
                    if (opening.Failure is not null)
                        return opening.Failure;
                    return opening.Granted.Contains(uri)
                        ? null
                        : "it didn't acknowledge a subscription to it; the resource may not exist, or this agent may not be allowed to watch it";
                }

                next = stream.NextOpening;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "it didn't acknowledge the subscription in time";
        }
    }

    /// <summary><c>mcp_unwatch_resource</c>: stops a watch of the asking session.</summary>
    private async Task<McpWatchResourceResponse> UnwatchResourceAsync(McpUnwatchResourceRequest req, CancellationToken ct)
    {
        var uri = req.Uri?.Trim() ?? "";
        var watch = req.SessionId is null ? null : FindWatch(req.SessionId, req.ServerName ?? "", uri);
        if (watch is null)
        {
            return new McpWatchResourceResponse
            {
                ServerName = req.ServerName ?? "", Uri = uri,
                Error = McpWatchText.NotWatching(req.ServerName ?? "", uri,
                    req.SessionId is null ? [] : SessionWatchEntries(req.SessionId)),
            };
        }

        await _watchGate.WaitAsync(ct);
        try
        {
            await EndWatchAsync(watch, "the agent stopped watching it", notify: false);
        }
        finally
        {
            _watchGate.Release();
        }

        _logger.LogInformation("Session {Session} stopped watching {Server} {Uri} ({WatchId})",
            watch.Entry.SessionId, watch.Entry.Server, watch.Entry.Uri, watch.WatchId);
        return new McpWatchResourceResponse
        {
            ServerName = watch.Entry.Server, Uri = watch.Entry.Uri, WatchId = watch.WatchId,
            Message = McpWatchText.Unwatched(watch.Entry),
        };
    }

    // ── Streams ─────────────────────────────────────────────────────────────

    /// <summary>The server's stream, started if it isn't running.</summary>
    private WatchStream EnsureWatchStream(string server)
    {
        lock (_watchStreams)
        {
            if (_watchStreams.TryGetValue(server, out var existing) && !existing.Stopped)
                return existing;

            var stream = new WatchStream(server, _lifetime.Token);
            _watchStreams[server] = stream;
            stream.Loop = RunWatchStreamAsync(stream);
            TrackParked(stream.Loop);
            return stream;
        }
    }

    private void StopWatchStream(WatchStream stream)
    {
        lock (_watchStreams)
        {
            if (_watchStreams.TryGetValue(stream.Server, out var current) && ReferenceEquals(current, stream))
                _watchStreams.Remove(stream.Server);
        }

        stream.Stop();
    }

    private WatchStream? GetWatchStream(string server)
    {
        lock (_watchStreams)
            return _watchStreams.TryGetValue(server, out var stream) && !stream.Stopped ? stream : null;
    }

    /// <summary>Distinct URIs watched on <paramref name="server"/>.</summary>
    private List<string> WatchedUris(string server) =>
        [.. _watches.Values
            .Where(w => string.Equals(w.Entry.Server, server, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Entry.Uri)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Keeps the server's stream open for as long as anything on it is watched, reopening it each
    /// time it ends: at once after a stream that lived, with back-off after one that failed fast.
    /// </summary>
    private async Task RunWatchStreamAsync(WatchStream stream)
    {
        await Task.Yield();
        var ct = stream.LoopCts.Token;
        var minDelay = TimeSpan.FromMilliseconds(Math.Max(10, _options.SubscriptionReconnectMinMs));
        var maxDelay = TimeSpan.FromMilliseconds(Math.Max(minDelay.TotalMilliseconds, _options.SubscriptionReconnectMaxMs));
        var delay = minDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var version = stream.Version;
                var wake = stream.WakeTask;
                var uris = WatchedUris(stream.Server);
                if (uris.Count == 0)
                {
                    StopWatchStream(stream);
                    return;
                }

                WatchStreamEnd end;
                var opened = _time.GetUtcNow();
                using (var lease = _connections.Lease(stream.Server))
                {
                    if (lease is null)
                    {
                        // Not connected: wait for the server to come back (its connect wakes the loop).
                        stream.Opened(new WatchStreamOpening(uris.ToHashSet(StringComparer.Ordinal),
                            new HashSet<string>(), McpCallDiagnostics.DescribeUnavailableServer(stream.Server)));
                        await WaitForWakeAsync(delay, wake, ct);
                        delay = Min(delay * 2, maxDelay);
                        continue;
                    }

                    if (!lease.Server.Summary.Subscriptions)
                    {
                        stream.Opened(new WatchStreamOpening(uris.ToHashSet(StringComparer.Ordinal), new HashSet<string>(),
                            "the server no longer offers resource subscriptions"));
                        await EndServerWatchesAsync(stream.Server, null,
                            $"the MCP server \"{stream.Server}\" no longer offers resource subscriptions");
                        return;
                    }

                    end = await RunOneStreamAsync(stream, lease.Server, uris, version, ct);
                }

                if (end == WatchStreamEnd.Unsupported)
                {
                    await EndServerWatchesAsync(stream.Server, null,
                        $"the MCP server \"{stream.Server}\" doesn't support subscriptions/listen (it may run an older protocol revision)");
                    return;
                }

                if (end == WatchStreamEnd.Recycled || _time.GetUtcNow() - opened >= WatchStreamHealthyLife)
                {
                    delay = minDelay;
                    continue;
                }

                await WaitForWakeAsync(delay, stream.WakeTask, ct);
                delay = Min(delay * 2, maxDelay);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The watch stream for MCP server {Server} failed; reopening it", stream.Server);
                try
                {
                    await WaitForWakeAsync(delay, stream.WakeTask, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = Min(delay * 2, maxDelay);
            }
        }
    }

    private enum WatchStreamEnd
    {
        /// <summary>The server ended it, its age ran out, or its connection was replaced.</summary>
        Ended,

        /// <summary>It failed or wasn't acknowledged.</summary>
        Failed,

        /// <summary>The bridge closed it to open it again with other URIs or a new token.</summary>
        Recycled,

        /// <summary>The server doesn't know <c>subscriptions/listen</c>.</summary>
        Unsupported,
    }

    /// <summary>One opening of a server's stream, until it ends.</summary>
    private async Task<WatchStreamEnd> RunOneStreamAsync(
        WatchStream stream, ConnectedServer server, List<string> uris, int version, CancellationToken ct)
    {
        var requested = uris.ToHashSet(StringComparer.Ordinal);

        // The watches this opening catches up: those confirmed before it began. One still being
        // added was read for its baseline a moment ago, and is its request's to confirm.
        var catchUp = _watches.Values
            .Where(w => w.Confirmed && string.Equals(w.Entry.Server, server.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var subscriptionId = "rbw_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ack = stream.BeginOpening(current, subscriptionId, version);
        Task<JsonRpcResponse>? listen = null;

        try
        {
            var parameters = new SubscriptionsListenRequestParams
            {
                Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = uris },
            };
            listen = server.Client.SendRequestAsync(new JsonRpcRequest
            {
                Id = new RequestId(subscriptionId),
                Method = RequestMethods.SubscriptionsListen,
                Params = JsonSerializer.SerializeToNode(parameters,
                    McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(SubscriptionsListenRequestParams))),
            }, current.Token);

            var ackTimeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.SubscriptionAckTimeoutSeconds)), _time, current.Token);
            var first = await Task.WhenAny(listen, ack.Task, ackTimeout);
            if (first != ack.Task)
            {
                var (end, failure) = first == listen
                    ? ClassifyStreamEnd(listen, current, ct)
                    : current.IsCancellationRequested && !ct.IsCancellationRequested
                        ? (WatchStreamEnd.Recycled, "the stream was reopened")
                        : (WatchStreamEnd.Failed, "it didn't acknowledge the subscription in time");
                ct.ThrowIfCancellationRequested();

                // A recycled opening isn't an outcome: the next one, with the current URIs, is.
                if (end == WatchStreamEnd.Recycled)
                    return end;

                stream.Opened(new WatchStreamOpening(requested, new HashSet<string>(), failure ?? "the stream ended before it was acknowledged"));
                _logger.LogWarning("Watch stream for MCP server {Server} didn't open: {Failure}", server.Name, failure);
                return end == WatchStreamEnd.Ended ? WatchStreamEnd.Failed : end;
            }

            var granted = (await ack.Task).Where(requested.Contains).ToHashSet(StringComparer.Ordinal);
            stream.Opened(new WatchStreamOpening(requested, granted, null));
            _logger.LogInformation("Watch stream for MCP server {Server} open: {Granted} of {Requested} resource(s) acknowledged",
                server.Name, granted.Count, requested.Count);

            // Anything that changed while there was no stream (or the agent was down) is found now.
            CatchUpWatches(catchUp, granted);

            var maxAge = Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.SubscriptionStreamMaxMinutes)), _time, current.Token);
            var replaced = WaitForConnectionReplacedAsync(server.Name, server.Client, current.Token);
            var done = await Task.WhenAny(listen, maxAge, replaced);
            ct.ThrowIfCancellationRequested();

            if (current.IsCancellationRequested)
                return WatchStreamEnd.Recycled;

            if (done == listen)
            {
                var (end, failure) = ClassifyStreamEnd(listen, current, ct);
                if (end == WatchStreamEnd.Unsupported)
                    return end;
                _logger.LogInformation("Watch stream for MCP server {Server} ended{Reason}; reopening",
                    server.Name, failure is null ? "" : $" ({failure})");
                return WatchStreamEnd.Ended;
            }

            _logger.LogDebug("Watch stream for MCP server {Server} closed by the bridge ({Why}); reopening",
                server.Name, done == maxAge ? "its maximum age" : "a new connection");
            return WatchStreamEnd.Ended;
        }
        finally
        {
            stream.EndOpening(current);
            try
            {
                // Ends the request: the SDK tells the server, which closes the stream.
                await current.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            if (listen is not null)
                await ((Task)listen).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>How a finished listen request ended.</summary>
    private static (WatchStreamEnd End, string? Failure) ClassifyStreamEnd(
        Task<JsonRpcResponse> listen, CancellationTokenSource current, CancellationToken loop)
    {
        if (listen.IsCompletedSuccessfully)
            return (WatchStreamEnd.Ended, null);

        if (listen.IsCanceled || (current.IsCancellationRequested && !loop.IsCancellationRequested))
            return (WatchStreamEnd.Recycled, "the stream was reopened");

        var ex = listen.Exception?.GetBaseException();
        return ex switch
        {
            McpProtocolException { ErrorCode: McpErrorCode.MethodNotFound } p => (WatchStreamEnd.Unsupported, p.Message),
            McpProtocolException p => (WatchStreamEnd.Failed, p.Message),
            null => (WatchStreamEnd.Failed, null),
            _ => (WatchStreamEnd.Failed, ex.Message),
        };
    }

    /// <summary>Completes when <paramref name="client"/> is no longer the server's current connection, or closes.</summary>
    private async Task WaitForConnectionReplacedAsync(string server, McpClient client, CancellationToken ct)
    {
        while (true)
        {
            await Task.WhenAny(client.Completion, Task.Delay(WatchConnectionCheckInterval, ct));
            ct.ThrowIfCancellationRequested();
            if (client.Completion.IsCompleted
                || !_connections.TryGet(server, out var currentServer)
                || !ReferenceEquals(currentServer.Client, client))
            {
                return;
            }
        }
    }

    private async Task WaitForWakeAsync(TimeSpan delay, Task wake, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(Task.Delay(delay, _time, cancel.Token), wake);
        await cancel.CancelAsync();
        ct.ThrowIfCancellationRequested();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // ── Notifications ───────────────────────────────────────────────────────

    /// <summary>
    /// Client notification handlers for a server's listen stream: its acknowledgement and its
    /// resource updates. Notifications tagged with another stream's id (an old stream still closing)
    /// are ignored.
    /// </summary>
    private IEnumerable<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>> WatchNotificationHandlers(string name) =>
    [
        new(NotificationMethods.SubscriptionsAcknowledgedNotification, (notification, _) =>
        {
            if (GetWatchStream(name) is { } stream)
            {
                var granted = (notification.Params?["notifications"]?["resourceSubscriptions"] as JsonArray ?? [])
                    .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                    .OfType<string>()
                    .ToList();
                stream.Acknowledge(SubscriptionIdOf(notification), granted);
            }

            return ValueTask.CompletedTask;
        }),
        new(NotificationMethods.ResourceUpdatedNotification, (notification, _) =>
        {
            var uri = notification.Params?["uri"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (uri is not null && GetWatchStream(name) is { } stream && stream.IsCurrent(SubscriptionIdOf(notification)))
                OnResourceUpdated(name, uri);
            return ValueTask.CompletedTask;
        }),
    ];

    private static string? SubscriptionIdOf(JsonRpcNotification notification) =>
        notification.Params?["_meta"]?[MetaKeys.SubscriptionId] is JsonValue id
            ? id.TryGetValue<string>(out var s) ? s : id.ToJsonString()
            : null;

    private void OnResourceUpdated(string server, string uri)
    {
        var debounce = TimeSpan.FromMilliseconds(Math.Max(0, _options.WatchDebounceMs));
        foreach (var watch in _watches.Values)
        {
            if (watch.Confirmed
                && string.Equals(watch.Entry.Server, server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(watch.Entry.Uri, uri, StringComparison.Ordinal))
            {
                ScheduleWatchRead(watch, debounce);
            }
        }
    }

    /// <summary>
    /// After a stream opens: reads every watched resource it acknowledged, to deliver what changed
    /// while there was no stream, and ends the watches it no longer acknowledges.
    /// </summary>
    private void CatchUpWatches(IReadOnlyList<ActiveWatch> watches, IReadOnlySet<string> granted)
    {
        foreach (var watch in watches)
        {
            if (!_watches.ContainsKey(watch.WatchId))
                continue;

            if (granted.Contains(watch.Entry.Uri))
                ScheduleWatchRead(watch, TimeSpan.Zero);
            else
                TrackParked(EndWatchAsync(watch, "the server no longer accepts a subscription to it", notify: true));
        }
    }

    // ── Reading and delivering ──────────────────────────────────────────────

    /// <summary>Reads the watch's resource after <paramref name="delay"/>; updates meanwhile fold into that one read.</summary>
    private void ScheduleWatchRead(ActiveWatch watch, TimeSpan delay)
    {
        if (Interlocked.Exchange(ref watch.ReadScheduled, 1) == 1)
            return;

        TrackParked(ReadAfterAsync());

        async Task ReadAfterAsync()
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _time, _lifetime.Token);
                else
                    await Task.Yield();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Volatile.Write(ref watch.ReadScheduled, 0);
            await ReadAndDeliverAsync(watch);
        }
    }

    /// <summary>
    /// Reads the watched resource and, when its content differs from what the session last got,
    /// delivers it. The hash is recorded only once the delivery went out (at least once).
    /// </summary>
    private async Task ReadAndDeliverAsync(ActiveWatch watch)
    {
        var ct = _lifetime.Token;
        try
        {
            await watch.ReadGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var entry = watch.Entry;
            if (!_watches.ContainsKey(watch.WatchId))
                return;

            if (_time.GetUtcNow() >= entry.ExpiresAt)
            {
                await EndWatchAsync(watch, ExpiredReason(entry), notify: true);
                return;
            }

            ReadResourceResult result;
            using (var lease = _connections.Lease(entry.Server))
            {
                // Not connected: the stream reopens when the server is back, and its catch-up reads this.
                if (lease is null)
                    return;

                try
                {
                    result = await ReadWatchedAsync(lease.Server, entry.Uri, ct);
                    watch.ReadFailures = 0;
                }
                catch (McpProtocolException ex) when (ex.ErrorCode is McpErrorCode.ResourceNotFound or McpErrorCode.InvalidParams)
                {
                    await EndWatchAsync(watch, $"the server says the resource no longer exists ({ex.Message})", notify: true);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    var failures = ++watch.ReadFailures;
                    _logger.LogWarning(ex, "Reading watched {Server} {Uri} failed ({Failures}/{Limit})",
                        entry.Server, entry.Uri, failures, WatchReadFailureLimit);
                    if (failures < WatchReadFailureLimit)
                        ScheduleWatchRead(watch, TimeSpan.FromMilliseconds(Math.Max(10, _options.SubscriptionReconnectMinMs) * (1 << failures)));
                    return;
                }
            }

            var hash = HashContents(result);
            if (string.Equals(hash, entry.LastHash, StringComparison.Ordinal))
                return;

            var delivered = await PublishTaskResultAsync(new McpTaskResult
            {
                SessionId = entry.SessionId,
                TaskId = entry.WatchId,
                ServerName = entry.Server,
                ToolName = entry.Uri,
                Kind = McpTaskResult.KindResourceUpdated,
                Content = RenderContents(result),
                ResourceUri = entry.Uri,
                ContentHash = hash,
                RunOrigin = entry.RunOrigin,
                UserAskedFor = entry.UserAskedFor,
            }, ct);

            if (!delivered)
            {
                // The hash is unchanged, so the next read delivers it again.
                ScheduleWatchRead(watch, TimeSpan.FromMilliseconds(Math.Max(10, _options.SubscriptionReconnectMinMs)));
                return;
            }

            entry.LastHash = hash;
            try
            {
                await _watchLedger.SetLastHashAsync(entry.WatchId, hash, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // At worst a restart delivers this version again; its hash lets the session see it's a repeat.
                _logger.LogWarning(ex, "Could not record the delivery of watch {WatchId}", entry.WatchId);
            }

            _logger.LogInformation("← MCP {Server} {Uri} changed; delivered to {Session} (watch {WatchId})",
                entry.Server, entry.Uri, entry.SessionId, entry.WatchId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a change to watch {WatchId} failed", watch.WatchId);
        }
        finally
        {
            watch.ReadGate.Release();
        }
    }

    private async Task<ReadResourceResult> ReadWatchedAsync(ConnectedServer server, string uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeoutMs(server.Config));
        return await server.Client.ReadResourceAsync(uri, cancellationToken: timeout.Token);
    }

    /// <summary>A hash of a read's contents: the same contents always give the same hash, across restarts.</summary>
    internal static string HashContents(ReadResourceResult result)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new ReadResourceResult { Contents = result.Contents },
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ReadResourceResult)));
        return Convert.ToHexStringLower(SHA256.HashData(json))[..32];
    }

    /// <summary>A read's contents as text for the session. Binary contents are described, never inlined.</summary>
    internal static string RenderContents(ReadResourceResult result)
    {
        var contents = result.Contents ?? [];
        if (contents.Count == 0)
            return "(the resource has no content)";

        var builder = new StringBuilder();
        foreach (var item in contents)
        {
            if (contents.Count > 1)
            {
                if (builder.Length > 0)
                    builder.Append("\n\n");
                builder.Append("--- ").Append(item.Uri).Append(item.MimeType is null ? "" : $" ({item.MimeType})").Append(" ---\n");
            }

            builder.Append(item switch
            {
                TextResourceContents text => text.Text,
                BlobResourceContents blob =>
                    $"[binary content: {blob.MimeType ?? "unknown type"}, {McpBinaryPayload.Decode(blob.Blob).Length:N0} bytes; " +
                    "read it with mcp_read_resource to get it as a file]",
                _ => $"[contents of an unsupported kind ({item.GetType().Name}); not shown]",
            });
        }

        return builder.ToString();
    }

    // ── Endings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ends a watch: records it, adjusts the server's stream, and — when the agent didn't end it
    /// itself — tells the session.
    /// </summary>
    private async Task EndWatchAsync(ActiveWatch watch, string reason, bool notify)
    {
        if (!_watches.TryRemove(watch.WatchId, out _))
            return;

        var entry = watch.Entry;
        entry.Status = McpWatchEntryStatus.Ended;
        entry.Reason = reason;
        entry.NotifySession = notify;
        entry.EndedAt = _time.GetUtcNow();
        try
        {
            await _watchLedger.EndAsync(entry.WatchId, reason, notify, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the end of watch {WatchId}", entry.WatchId);
        }

        // The stream stops carrying the URI (or stops) once nothing watches it.
        if (GetWatchStream(entry.Server) is { } stream)
        {
            var remaining = WatchedUris(entry.Server);
            if (remaining.Count == 0)
                StopWatchStream(stream);
            else if (!remaining.Contains(entry.Uri, StringComparer.Ordinal))
                stream.Recycle();
        }

        _logger.LogInformation("Watch {WatchId} ({Server} {Uri}) for {Session} ended: {Reason}",
            entry.WatchId, entry.Server, entry.Uri, entry.SessionId, reason);

        if (notify)
            await DeliverWatchEndedAsync(entry);
    }

    /// <summary>Tells a session its watch ended, retrying until it goes out; then settles the entry.</summary>
    private async Task DeliverWatchEndedAsync(McpWatchEntry entry)
    {
        var message = new McpTaskResult
        {
            SessionId = entry.SessionId,
            TaskId = entry.WatchId,
            ServerName = entry.Server,
            ToolName = entry.Uri,
            Kind = McpTaskResult.KindWatchEnded,
            Content = McpWatchText.Ended(entry),
            ResourceUri = entry.Uri,
            RunOrigin = entry.RunOrigin,
            UserAskedFor = entry.UserAskedFor,
        };

        var delay = TimeSpan.FromMilliseconds(Math.Max(10, _options.SubscriptionReconnectMinMs));
        var max = TimeSpan.FromMilliseconds(Math.Max(delay.TotalMilliseconds, _options.SubscriptionReconnectMaxMs));
        var delivered = await PublishTaskResultAsync(message, CancellationToken.None);
        while (!delivered && !_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = Min(delay * 2, max);
            delivered = await PublishTaskResultAsync(message, CancellationToken.None);
        }

        if (!delivered)
            return; // The ledger still shows it undelivered; the next start delivers it.

        try
        {
            await _watchLedger.MarkSettledAsync(entry.WatchId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record that the end of watch {WatchId} was delivered", entry.WatchId);
        }
    }

    /// <summary>
    /// Ends every watch on a server: it was removed, its entry no longer allows watches or points
    /// elsewhere, or it stopped offering subscriptions.
    /// </summary>
    /// <param name="serverId">The entry's stable id, which finds watches recorded under an old name.</param>
    private async Task EndServerWatchesAsync(string serverName, string? serverId, string reason)
    {
        foreach (var watch in _watches.Values.ToList())
        {
            var entry = watch.Entry;
            if (string.Equals(entry.Server, serverName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(serverId) && string.Equals(entry.ServerId, serverId, StringComparison.Ordinal)))
            {
                // A watch not yet confirmed is still the request's: it is refused there, not announced.
                await EndWatchAsync(watch, reason, notify: watch.Confirmed);
            }
        }
    }

    /// <summary>A new connection to a server: its stream (if anything is watched there) reopens on it.</summary>
    private void OnWatchServerConnected(string name, bool offersWatches)
    {
        if (!_watches.Values.Any(w => string.Equals(w.Entry.Server, name, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!offersWatches)
        {
            TrackParked(EndServerWatchesAsync(name, null, $"the MCP server \"{name}\" no longer offers resource subscriptions"));
            return;
        }

        EnsureWatchStream(name).Recycle();
    }

    /// <summary>
    /// A server's OAuth tokens changed (a refresh or a new sign-in). Its server checks the bearer
    /// token only when a stream opens, so the stream reopens with the new one.
    /// </summary>
    private void OnOAuthTokensStored(string serverUrl)
    {
        var url = McpBridgeServerConfig.NormalizeUrl(serverUrl);
        List<WatchStream> streams;
        lock (_watchStreams)
            streams = [.. _watchStreams.Values];

        foreach (var stream in streams)
        {
            if (_connections.TryGetConfig(stream.Server, out var config)
                && McpBridgeServerConfig.NormalizeUrl(config.Url) == url)
            {
                _logger.LogDebug("OAuth tokens for MCP server {Server} changed; reopening its watch stream", stream.Server);
                stream.Recycle();
            }
        }
    }

    // ── Startup and expiry ──────────────────────────────────────────────────

    /// <summary>
    /// Startup: every active watch in the ledger is watched again (its stream opens as its server
    /// connects, and the catch-up delivers what changed meanwhile), and every ending that hadn't
    /// reached its session is delivered.
    /// </summary>
    private async Task ResumeWatchesAsync(CancellationToken ct)
    {
        IReadOnlyList<McpWatchEntry> entries;
        try
        {
            entries = await _watchLedger.ReadAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read the MCP watch ledger at {Path}", _watchLedger.Path);
            entries = [];
        }

        var now = _time.GetUtcNow();
        foreach (var entry in entries.Where(e => !e.IsSettled))
        {
            if (!entry.IsActive)
            {
                if (entry.NotifySession)
                    TrackParked(DeliverWatchEndedAsync(entry));
                continue;
            }

            // A server renamed while the agent was down is found by its entry's id.
            if (!_connections.IsConfigured(entry.Server) && entry.ServerId is { Length: > 0 } id
                && _connections.ConfiguredNames.FirstOrDefault(n =>
                    _connections.TryGetConfig(n, out var c) && string.Equals(c.Id, id, StringComparison.Ordinal)) is { } renamed)
            {
                entry.Server = renamed;
                await _watchLedger.UpdateAsync(entry.WatchId, e => e.Server = renamed, CancellationToken.None);
            }

            var watch = new ActiveWatch(entry, confirmed: true);
            _watches[entry.WatchId] = watch;

            if (!_connections.TryGetConfig(entry.Server, out var config))
                await EndWatchAsync(watch, $"its MCP server \"{entry.Server}\" is no longer configured", notify: true);
            else if (!config.WatchesResources)
                await EndWatchAsync(watch, $"the MCP server \"{entry.Server}\" no longer allows resource watches", notify: true);
            else if (now >= entry.ExpiresAt)
                await EndWatchAsync(watch, ExpiredReason(entry), notify: true);
        }

        foreach (var server in _watches.Values.Select(w => w.Entry.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            EnsureWatchStream(server);
            _logger.LogInformation("Resumed {Count} watch(es) on MCP server {Server} after a restart",
                _watches.Values.Count(w => string.Equals(w.Entry.Server, server, StringComparison.OrdinalIgnoreCase)), server);
        }

        TrackParked(RunWatchExpiryAsync());
    }

    /// <summary>Ends watches past their time-to-live, and tells their sessions.</summary>
    private async Task RunWatchExpiryAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchExpiryCheckInterval, _time, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ExpireWatchesAsync();
        }
    }

    /// <summary>Ends every watch past its time-to-live.</summary>
    internal async Task ExpireWatchesAsync()
    {
        var now = _time.GetUtcNow();
        foreach (var watch in _watches.Values.Where(w => now >= w.Entry.ExpiresAt).ToList())
            await EndWatchAsync(watch, ExpiredReason(watch.Entry), notify: true);
    }

    private static string ExpiredReason(McpWatchEntry entry) =>
        $"it expired after {(int)Math.Round((entry.ExpiresAt - entry.CreatedAt).TotalDays)} day(s)";

    // ── Lookups ─────────────────────────────────────────────────────────────

    private ActiveWatch? FindWatch(string sessionId, string server, string uri) =>
        _watches.Values.FirstOrDefault(w =>
            string.Equals(w.Entry.SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(w.Entry.Server, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(w.Entry.Uri, uri, StringComparison.Ordinal));

    private List<McpWatchEntry> SessionWatchEntries(string sessionId) =>
        [.. _watches.Values
            .Where(w => string.Equals(w.Entry.SessionId, sessionId, StringComparison.Ordinal))
            .Select(w => w.Entry)
            .OrderBy(e => e.CreatedAt)];

    /// <summary>The active watches, for tests.</summary>
    internal IReadOnlyList<McpWatchEntry> ActiveWatchEntries => [.. _watches.Values.Select(w => w.Entry)];

    private static string NewWatchId() => "w_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
}
