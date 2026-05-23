using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Agent.McpBridge.Auth;

namespace RockBot.Agent.Tests.McpBridge.Auth;

[TestClass]
public class WorkIqHealthTrackerTests
{
    private string _cacheDir = null!;
    private string _cachePath = null!;

    [TestInitialize]
    public void Init()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "rockbot-health-tracker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
        _cachePath = Path.Combine(_cacheDir, "workiq-cache.bin");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    [TestMethod]
    public void Initial_NoCacheFile_StartsUnhealthy()
    {
        var tracker = CreateTracker();
        Assert.IsFalse(tracker.IsHealthy);
    }

    [TestMethod]
    public void Initial_EmptyCacheFile_StartsUnhealthy()
    {
        File.WriteAllBytes(_cachePath, []);
        var tracker = CreateTracker();
        Assert.IsFalse(tracker.IsHealthy);
    }

    [TestMethod]
    public void Initial_NonEmptyCacheFile_StartsHealthy()
    {
        File.WriteAllBytes(_cachePath, [1, 2, 3]);
        var tracker = CreateTracker();
        Assert.IsTrue(tracker.IsHealthy);
        Assert.AreEqual("startup_cache_present", tracker.LastReason);
    }

    [TestMethod]
    public void MarkUnhealthy_FromHealthy_FlipsAndRaisesEvent()
    {
        File.WriteAllBytes(_cachePath, [1, 2, 3]);
        var tracker = CreateTracker();
        WorkIqHealthTracker.HealthChangedArgs? captured = null;
        tracker.HealthChanged += args => captured = args;

        tracker.MarkUnhealthy("refresh_revoked:test");

        Assert.IsFalse(tracker.IsHealthy);
        Assert.AreEqual("refresh_revoked:test", tracker.LastReason);
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured!.OldValue);
        Assert.IsFalse(captured.NewValue);
        Assert.AreEqual("refresh_revoked:test", captured.Reason);
    }

    [TestMethod]
    public void MarkHealthy_FromUnhealthy_FlipsAndRaisesEvent()
    {
        var tracker = CreateTracker();
        WorkIqHealthTracker.HealthChangedArgs? captured = null;
        tracker.HealthChanged += args => captured = args;

        tracker.MarkHealthy("cache_updated_from_ui");

        Assert.IsTrue(tracker.IsHealthy);
        Assert.AreEqual("cache_updated_from_ui", tracker.LastReason);
        Assert.IsNotNull(captured);
        Assert.IsFalse(captured!.OldValue);
        Assert.IsTrue(captured.NewValue);
    }

    [TestMethod]
    public void MarkHealthy_WhenAlreadyHealthy_DoesNotRaiseEvent()
    {
        File.WriteAllBytes(_cachePath, [1, 2, 3]);
        var tracker = CreateTracker();
        var raised = 0;
        tracker.HealthChanged += _ => raised++;

        tracker.MarkHealthy("again");

        Assert.AreEqual(0, raised);
        Assert.IsTrue(tracker.IsHealthy);
    }

    [TestMethod]
    public void MarkUnhealthy_WhenAlreadyUnhealthy_DoesNotRaiseEvent()
    {
        var tracker = CreateTracker();
        var raised = 0;
        tracker.HealthChanged += _ => raised++;

        tracker.MarkUnhealthy("still unhealthy");

        Assert.AreEqual(0, raised);
        Assert.IsFalse(tracker.IsHealthy);
    }

    [TestMethod]
    public void Transition_EmitsLogLineOnFlip()
    {
        File.WriteAllBytes(_cachePath, [1, 2, 3]);
        var capturing = new CapturingLogger();
        var tracker = new WorkIqHealthTracker(BuildOptions(), capturing);

        tracker.MarkUnhealthy("refresh_revoked");
        tracker.MarkHealthy("cache_updated_from_ui");

        var flipLines = capturing.Messages.Where(m => m.Contains("WorkIQ auth health changed")).ToList();
        Assert.AreEqual(2, flipLines.Count, "expected one log line per actual transition");
        StringAssert.Contains(flipLines[0], "Healthy=True");
        StringAssert.Contains(flipLines[0], "False");
        StringAssert.Contains(flipLines[0], "refresh_revoked");
        StringAssert.Contains(flipLines[1], "cache_updated_from_ui");
    }

    [TestMethod]
    public void NoOpTransition_DoesNotEmitLogLine()
    {
        var capturing = new CapturingLogger();
        var tracker = new WorkIqHealthTracker(BuildOptions(), capturing);

        tracker.MarkUnhealthy("first");
        tracker.MarkUnhealthy("second");

        var flipLines = capturing.Messages.Where(m => m.Contains("WorkIQ auth health changed")).ToList();
        Assert.AreEqual(0, flipLines.Count, "starts unhealthy; two unhealthy calls should produce no log lines");
    }

    private WorkIqHealthTracker CreateTracker() =>
        new(BuildOptions(), NullLogger<WorkIqHealthTracker>.Instance);

    private IOptions<MsalTokenProviderOptions> BuildOptions() =>
        Options.Create(new MsalTokenProviderOptions
        {
            CacheFilePath = _cachePath,
            TenantId = "00000000-0000-0000-0000-000000000002",
            ClientId = "00000000-0000-0000-0000-000000000001"
        });

    private sealed class CapturingLogger : ILogger<WorkIqHealthTracker>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
