using ModelContextProtocol.Client;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The lifecycle rules behind #604: a reader's snapshot is consistent, a replaced connection is
/// disposed exactly once and never while a call holds it, and writers for one server run one at
/// a time.
/// </summary>
[TestClass]
public class McpServerConnectionsTests
{
    private sealed class TrackedDisposable : IAsyncDisposable
    {
        private int _disposals;
        public int Disposals => _disposals;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A snapshot whose resources own <paramref name="owned"/>. The connection logic under test
    /// never touches the client itself, so these snapshots don't carry one.
    /// </summary>
    private static ConnectedServer Snapshot(string name, TrackedDisposable owned, ConnectionResources? resources = null)
    {
        resources ??= new ConnectionResources(owned);
        return new ConnectedServer
        {
            Name = name,
            Config = new McpBridgeServerConfig { Type = "http", Url = "http://localhost/mcp" },
            Client = null!,
            Tools = [],
            Prompts = [],
            Metadata = new McpServerMetadata(null, null, null, null, null),
            Summary = new McpServerSummary { ServerName = name },
            AttachmentGateway = new Lazy<AttachmentGateway?>(() => null),
            Resources = resources
        };
    }

    private static async Task DisposedWithin(ConnectionResources resources) =>
        await resources.Disposed.WaitAsync(TimeSpan.FromSeconds(5));

    [TestMethod]
    public async Task ReplacedConnection_IsDisposedOnce()
    {
        var connections = new McpServerConnections();
        var first = new TrackedDisposable();
        var firstSnapshot = Snapshot("adjutant", first);
        connections.Publish(firstSnapshot);

        connections.Publish(Snapshot("adjutant", new TrackedDisposable()));
        await DisposedWithin(firstSnapshot.Resources);

        Assert.AreEqual(1, first.Disposals);
        firstSnapshot.Resources.Retire();
        Assert.AreEqual(1, first.Disposals, "Retiring twice must not dispose twice.");
    }

    [TestMethod]
    public async Task ReplacedConnection_IsNotDisposedWhileACallHoldsIt()
    {
        var connections = new McpServerConnections();
        var first = new TrackedDisposable();
        connections.Publish(Snapshot("adjutant", first));

        var lease = connections.Lease("adjutant")!;
        connections.Publish(Snapshot("adjutant", new TrackedDisposable()));
        await Task.Delay(50);

        Assert.AreEqual(0, first.Disposals, "A call in flight keeps its connection alive.");
        Assert.IsTrue(lease.Server.Resources.IsRetired);

        lease.Dispose();
        await DisposedWithin(lease.Server.Resources);
        Assert.AreEqual(1, first.Disposals);
    }

    [TestMethod]
    public void Lease_AfterAReplacement_GetsTheNewConnection()
    {
        var connections = new McpServerConnections();
        connections.Publish(Snapshot("adjutant", new TrackedDisposable()));
        var replacement = Snapshot("adjutant", new TrackedDisposable());
        connections.Publish(replacement);

        using var lease = connections.Lease("adjutant");

        Assert.AreSame(replacement, lease!.Server);
    }

    [TestMethod]
    public void RetiredConnection_CannotBeLeasedAgain()
    {
        var resources = new ConnectionResources(new TrackedDisposable());
        resources.Retire();

        Assert.IsFalse(resources.TryAcquire());
    }

    [TestMethod]
    public async Task Republishing_TheSameConnection_RetiresNothing()
    {
        // A surface refresh publishes a new snapshot of the same connection.
        var connections = new McpServerConnections();
        var owned = new TrackedDisposable();
        var original = Snapshot("adjutant", owned);
        connections.Publish(original);

        connections.Publish(original with { Tools = [] });
        await Task.Delay(50);

        Assert.IsFalse(original.Resources.IsRetired);
        Assert.AreEqual(0, owned.Disposals);
    }

    [TestMethod]
    public async Task Remove_RetiresTheConnectionAndOptionallyForgetsTheConfig()
    {
        var connections = new McpServerConnections();
        var owned = new TrackedDisposable();
        var snapshot = Snapshot("adjutant", owned);
        connections.SetConfig("adjutant", snapshot.Config);
        connections.Publish(snapshot);

        connections.Remove("adjutant", forgetConfig: false);
        await DisposedWithin(snapshot.Resources);

        Assert.IsFalse(connections.IsConnected("adjutant"));
        Assert.IsTrue(connections.IsConfigured("adjutant"), "A disconnected server stays configured for the reconnect sweep.");

        connections.Remove("adjutant", forgetConfig: true);
        Assert.IsFalse(connections.IsConfigured("adjutant"));
        Assert.AreEqual(1, owned.Disposals);
    }

    [TestMethod]
    public async Task ResourceAddedAfterDisposal_IsDisposedImmediately()
    {
        var resources = new ConnectionResources(new TrackedDisposable());
        resources.Retire();
        await DisposedWithin(resources);

        var late = new TrackedDisposable();
        resources.Add(late);
        await Task.Delay(50);

        Assert.AreEqual(1, late.Disposals);
    }

    [TestMethod]
    public async Task LockAsync_RunsOneWriterPerServerAtATime()
    {
        var connections = new McpServerConnections();
        var active = 0;
        var maxActive = 0;

        async Task Writer()
        {
            using var _ = await connections.LockAsync("adjutant", CancellationToken.None);
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(Writer)));

        Assert.AreEqual(1, maxActive);
    }

    [TestMethod]
    public async Task LockAsync_DoesNotSerialiseDifferentServers()
    {
        var connections = new McpServerConnections();
        using var adjutant = await connections.LockAsync("adjutant", CancellationToken.None);

        var other = connections.LockAsync("todo-mcp", CancellationToken.None);

        Assert.IsTrue(other.Wait(TimeSpan.FromSeconds(1)), "A different server's lock must be free.");
        other.Result.Dispose();
    }

    [TestMethod]
    public async Task RetireAll_WaitsForCallsInFlight()
    {
        var connections = new McpServerConnections();
        var owned = new TrackedDisposable();
        connections.Publish(Snapshot("adjutant", owned));
        var lease = connections.Lease("adjutant")!;

        var retiring = connections.RetireAllAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.IsFalse(retiring.IsCompleted, "Shutdown waits for the call to finish.");

        lease.Dispose();
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, owned.Disposals);
    }

    [TestMethod]
    public async Task ConcurrentLeasesAndReplacements_NeverDisposeALeasedConnection()
    {
        var connections = new McpServerConnections();
        var ownedBy = new System.Collections.Concurrent.ConcurrentDictionary<ConnectionResources, TrackedDisposable>();
        ConnectedServer Tracked()
        {
            var owned = new TrackedDisposable();
            var snapshot = Snapshot("adjutant", owned);
            ownedBy[snapshot.Resources] = owned;
            return snapshot;
        }

        connections.Publish(Tracked());
        var violations = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var lease = connections.Lease("adjutant");
                if (lease is null) continue;
                var owned = ownedBy[lease.Server.Resources];
                await Task.Yield();
                if (owned.Disposals > 0) Interlocked.Increment(ref violations);
            }
        })).ToList();

        var replacer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using (await connections.LockAsync("adjutant", CancellationToken.None))
                    connections.Publish(Tracked());
                await Task.Yield();
            }
        });

        await Task.WhenAll([.. callers, replacer]);

        Assert.AreEqual(0, violations, "A connection was disposed while a call held it.");
    }


    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
