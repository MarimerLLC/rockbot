using RockBot.UserProxy;

namespace RockBot.Host.Tests;

/// <summary>
/// #841: with a file, session origins survive a restart, so a result that lands after one still
/// has its anchor.
/// </summary>
[TestClass]
public class SessionOriginStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private string _dir = null!;
    private string _path = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-origins-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, SessionOriginStore.FileName);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SessionOriginStore Open() => new(_path, timeProvider: new FixedTime(Now));

    private static ReplyOrigin Origin(string session, string summary = "research X", DateTimeOffset? at = null) =>
        new("cli", summary, at ?? Now.AddMinutes(-5), session);

    [TestMethod]
    public void InMemory_WritesNoFile()
    {
        var store = new SessionOriginStore();
        store.Set("s1", Origin("s1"));

        Assert.AreEqual(Origin("s1"), store.Get("s1"));
        Assert.IsFalse(Directory.Exists(_dir));
    }

    [TestMethod]
    public void Origins_RoundTripThroughTheFile()
    {
        Open().Set("cli-session", Origin("cli-session"));

        var reloaded = Open();

        Assert.AreEqual(Origin("cli-session"), reloaded.Get("cli-session"));
        Assert.AreEqual(Origin("cli-session"), reloaded.Get("CLI-SESSION"), "lookups stay case-insensitive");
    }

    [TestMethod]
    public void FirstWriterWins_AfterAReload()
    {
        Open().Set("s1", Origin("s1", "first"));

        var reloaded = Open();
        reloaded.Set("s1", Origin("s1", "second"));

        Assert.AreEqual("first", reloaded.Get("s1")?.PromptSummary);
        Assert.AreEqual("first", Open().Get("s1")?.PromptSummary);
    }

    [TestMethod]
    public void Clear_IsSaved()
    {
        var store = Open();
        store.Set("s1", Origin("s1"));
        store.Set("s2", Origin("s2"));
        store.Clear("s1");

        var reloaded = Open();

        Assert.IsNull(reloaded.Get("s1"));
        Assert.IsNotNull(reloaded.Get("s2"));
    }

    [TestMethod]
    public void OldEntries_ArePruned()
    {
        var store = Open();
        store.Set("old", Origin("old", at: Now - SessionOriginStore.MaxAge - TimeSpan.FromHours(1)));
        store.Set("new", Origin("new"));

        var reloaded = Open();

        Assert.IsNull(reloaded.Get("old"));
        Assert.IsNotNull(reloaded.Get("new"));
    }

    [TestMethod]
    public void AtMostMaxEntries_OldestDropped()
    {
        const int max = 3;
        var store = new SessionOriginStore(_path, null, new FixedTime(Now), max);
        for (var i = 0; i <= max; i++)
            store.Set($"s{i}", Origin($"s{i}", at: Now.AddMinutes(-max - 1 + i)));

        var reloaded = Open();

        Assert.IsNull(reloaded.Get("s0"), "the oldest went");
        Assert.IsNotNull(reloaded.Get("s1"));
        Assert.IsNotNull(reloaded.Get($"s{max}"));
    }

    [TestMethod]
    public void CorruptFile_IsMovedAside_AndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{ not json");

        var store = Open();

        Assert.IsNull(store.Get("s1"));
        Assert.IsTrue(File.Exists(_path + ".corrupt"));
        store.Set("s1", Origin("s1"));
        Assert.IsNotNull(Open().Get("s1"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
