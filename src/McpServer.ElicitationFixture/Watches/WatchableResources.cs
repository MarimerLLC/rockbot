using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.ElicitationFixture.Watches;

/// <summary>
/// In-memory text resources at <c>fixture://watch/{name}</c> that can be changed on demand, and a
/// <c>subscriptions/listen</c> handler (SEP-2575) that pushes <c>notifications/resources/updated</c>
/// when they change. It lets RockBot's resource watches (<c>mcp_watch_resource</c>) be exercised
/// against a live agent, and the bridge's tests use the same class.
/// </summary>
/// <remarks>
/// The handler behaves the way the scheduler's does: it acknowledges only the URIs that exist,
/// tags every notification with the listen request's id, sends no keepalives, and ends each stream
/// itself after <see cref="StreamLifetime"/> so the client reconnects. The server holds no state
/// beyond the resources' text, reads no credentials and makes no outbound calls.
/// </remarks>
public sealed class WatchableResources
{
    public const string UriPrefix = "fixture://watch/";

    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Channel<string>, byte> _listeners = new();
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _streams = new();
    private int _streamsOpened;

    /// <summary>How long a listen stream stays open before the server ends it.</summary>
    public TimeSpan StreamLifetime { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>When true, changes aren't pushed: a client only finds them by reading again.</summary>
    public bool SuppressNotifications { get; set; }

    /// <summary>When true, <c>subscriptions/listen</c> fails as an older server's would (method not found).</summary>
    public bool RefuseListen { get; set; }

    /// <summary>URIs never acknowledged, as a server leaves out what the caller may not read.</summary>
    public ConcurrentDictionary<string, byte> Unacknowledged { get; } = new(StringComparer.Ordinal);

    /// <summary>How many listen streams have been opened.</summary>
    public int StreamsOpened => Volatile.Read(ref _streamsOpened);

    /// <summary>How many listen streams are open now.</summary>
    public int OpenStreams => _streams.Count;

    /// <summary>The URIs the latest listen request asked for.</summary>
    public IReadOnlyList<string> LastRequested { get; private set; } = [];

    public static string UriFor(string name) => UriPrefix + name;

    /// <summary>Sets (or creates) a resource's text, and pushes the change.</summary>
    public void Set(string name, string text)
    {
        _texts[name] = text;
        Notify(name);
    }

    /// <summary>Deletes a resource, and pushes that as a change (a read then fails as not found).</summary>
    public bool Delete(string name)
    {
        var removed = _texts.TryRemove(name, out _);
        if (removed)
            Notify(name);
        return removed;
    }

    public bool Exists(string name) => _texts.ContainsKey(name);

    /// <summary>Ends every open listen stream now, as a server restart or the stream lifetime would.</summary>
    public void EndStreams()
    {
        foreach (var stream in _streams.Keys)
        {
            try
            {
                stream.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Gives the server the resource template, the change tools, and the listen handler.</summary>
    public void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Resources.Subscribe = true;
        options.Handlers.SubscriptionsListenHandler = ListenAsync;
    }

    /// <summary>The resource template <c>fixture://watch/{name}</c>.</summary>
    public IEnumerable<McpServerResource> Resources() =>
    [
        McpServerResource.Create(Read, new McpServerResourceCreateOptions
        {
            UriTemplate = UriPrefix + "{name}",
            Name = "watch",
            Description = "A text resource that can be changed on demand with set_watch_resource, for testing resource watches.",
            MimeType = "text/plain",
        }),
    ];

    /// <summary>The tools that change the resources.</summary>
    public IEnumerable<McpServerTool> Tools() =>
    [
        McpServerTool.Create(SetWatchResource, new McpServerToolCreateOptions { Name = "set_watch_resource" }),
        McpServerTool.Create(DeleteWatchResource, new McpServerToolCreateOptions { Name = "delete_watch_resource" }),
    ];

    [Description("Sets the text of the watchable resource fixture://watch/{name}, creating it if needed. A client watching it is notified.")]
    private string SetWatchResource(
        [Description("The resource's name, e.g. 'poll'.")] string name,
        [Description("Its new text.")] string text)
    {
        Set(name, text);
        return $"Set {UriFor(name)} ({text.Length} chars).";
    }

    [Description("Deletes the watchable resource fixture://watch/{name}. A client watching it is notified, and its next read fails as not found.")]
    private string DeleteWatchResource([Description("The resource's name.")] string name) =>
        Delete(name) ? $"Deleted {UriFor(name)}." : $"{UriFor(name)} doesn't exist.";

    private string Read(string name) =>
        _texts.TryGetValue(name, out var text)
            ? text
            : throw new McpProtocolException($"Resource {UriFor(name)} not found.", McpErrorCode.ResourceNotFound);

    private void Notify(string name)
    {
        if (SuppressNotifications)
            return;
        foreach (var listener in _listeners.Keys)
            listener.Writer.TryWrite(UriFor(name));
    }

    /// <summary>The <c>subscriptions/listen</c> handler: acknowledge, then push changes until the stream's lifetime ends.</summary>
    public async ValueTask<EmptyResult> ListenAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        if (RefuseListen)
            throw new McpProtocolException("Method 'subscriptions/listen' is not available.", McpErrorCode.MethodNotFound);

        var requested = (request.Params?.Notifications?.ResourceSubscriptions ?? []).Distinct(StringComparer.Ordinal).ToList();
        LastRequested = requested;

        // Listen before acknowledging, so a change right after the acknowledgement isn't missed.
        var changes = Channel.CreateUnbounded<string>();
        _listeners[changes] = 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _streams[lifetime] = 0;
        Interlocked.Increment(ref _streamsOpened);
        try
        {
            var granted = requested
                .Where(uri => uri.StartsWith(UriPrefix, StringComparison.Ordinal)
                              && Exists(uri[UriPrefix.Length..])
                              && !Unacknowledged.ContainsKey(uri))
                .ToHashSet(StringComparer.Ordinal);

            var subscriptionId = JsonSerializer.SerializeToNode(request.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions);
            var ack = JsonSerializer.SerializeToNode(
                new SubscriptionsAcknowledgedNotificationParams
                {
                    Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = [.. granted] },
                },
                McpJsonUtilities.DefaultOptions)!.AsObject();
            ack["_meta"] = Tag(subscriptionId);
            await request.Server.SendNotificationAsync(
                NotificationMethods.SubscriptionsAcknowledgedNotification, ack, cancellationToken: cancellationToken);

            lifetime.CancelAfter(StreamLifetime);
            try
            {
                await foreach (var uri in changes.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (!granted.Contains(uri))
                        continue;
                    await request.Server.SendNotificationAsync(
                        NotificationMethods.ResourceUpdatedNotification,
                        new ResourceUpdatedNotificationParams { Uri = uri, Meta = Tag(subscriptionId) },
                        cancellationToken: lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The stream's lifetime is up, or it was ended: the client reconnects.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new EmptyResult();
        }
        finally
        {
            _streams.TryRemove(lifetime, out _);
            _listeners.TryRemove(changes, out _);
        }
    }

    private static JsonObject Tag(JsonNode? subscriptionId) => new() { [MetaKeys.SubscriptionId] = subscriptionId?.DeepClone() };
}
