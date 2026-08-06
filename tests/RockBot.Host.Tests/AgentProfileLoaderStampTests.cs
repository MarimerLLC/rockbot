namespace RockBot.Host.Tests;

[TestClass]
public class AgentProfileLoaderStampTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-profile-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void Stamp_IsStable_WhenNothingChanges()
    {
        Write("soul.md", "a");
        Write("directives.md", "b");

        Assert.AreEqual(
            AgentProfileLoader.ReadDirectoryStamp(_dir),
            AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_Changes_WhenContentLengthChanges()
    {
        Write("soul.md", "a");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        Write("soul.md", "aa");

        Assert.AreNotEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_Changes_WhenLastWriteTimeChangesButLengthDoesNot()
    {
        var path = Write("soul.md", "a");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        // Same length — only the timestamp moves. This is the edit an editor makes when
        // you change one character, so length alone is not a sufficient fingerprint.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));

        Assert.AreNotEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_Changes_WhenFileAdded()
    {
        Write("soul.md", "a");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        Write("style.md", "c");

        Assert.AreNotEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_Changes_WhenFileDeleted()
    {
        Write("soul.md", "a");
        Write("style.md", "c");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        File.Delete(Path.Combine(_dir, "style.md"));

        Assert.AreNotEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_IgnoresNonMarkdownFiles()
    {
        Write("soul.md", "a");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        // Memory, telemetry and logs churn constantly inside the profile directory;
        // only *.md feeds the profile, so they must not trigger reloads.
        Write("scheduled-tasks.json", "{}");

        Assert.AreEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_IgnoresSubdirectories()
    {
        Write("soul.md", "a");
        var before = AgentProfileLoader.ReadDirectoryStamp(_dir);

        var sub = Path.Combine(_dir, "memory");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "note.md"), "saved memory");

        Assert.AreEqual(before, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }

    [TestMethod]
    public void Stamp_IsOrderIndependent()
    {
        Write("b.md", "x");
        Write("a.md", "y");
        var first = AgentProfileLoader.ReadDirectoryStamp(_dir);

        Assert.AreEqual(first, AgentProfileLoader.ReadDirectoryStamp(_dir));
        Assert.IsTrue(first!.IndexOf("a.md", StringComparison.Ordinal)
                    < first.IndexOf("b.md", StringComparison.Ordinal),
            "entries should be sorted so directory enumeration order cannot flip the stamp");
    }

    [TestMethod]
    public void Stamp_ReturnsNull_WhenDirectoryMissing()
    {
        Assert.IsNull(AgentProfileLoader.ReadDirectoryStamp(
            Path.Combine(_dir, "does-not-exist")));
    }

    [TestMethod]
    public void Stamp_IsEmpty_ForDirectoryWithNoMarkdown()
    {
        Assert.AreEqual(string.Empty, AgentProfileLoader.ReadDirectoryStamp(_dir));
    }
}
