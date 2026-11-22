namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The retrying read the bridge end-to-end tests use on files the bridge rewrites (#863): a read
/// that lands while the file is held waits it out instead of failing with a sharing violation.
/// </summary>
[TestClass]
public class SharedFileTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-shared-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task AReadWhileTheFileIsHeld_WaitsForItToBeReleased()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Only Windows enforces share modes, so the file can't be held against a reader here.");

        var path = Path.Combine(_dir, "ledger.json");
        File.WriteAllText(path, "{ \"entries\": [] }");

        Task<string> read;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            read = Task.Run(() => SharedFile.ReadAllText(path));
            await Task.Delay(50);
            Assert.IsFalse(read.IsCompleted, "the read must still be waiting while the file is held");
        }

        Assert.AreEqual("{ \"entries\": [] }", await read);
    }

    [TestMethod]
    public void AMissingFile_IsNotRetried()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() => SharedFile.ReadAllText(Path.Combine(_dir, "absent.json")));
    }
}
