namespace RockBot.Tools.Tests;

[TestClass]
public class SharedVolumePathTests
{
    private string _base = null!;

    [TestInitialize]
    public void Init()
    {
        _base = Path.Combine(Path.GetTempPath(), "rb-shared-path-tests", Guid.NewGuid().ToString("N"), "shared");
        Directory.CreateDirectory(Path.Combine(_base, "exports"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(Path.GetDirectoryName(_base)!, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void RelativePath_ResolvesUnderTheBase()
    {
        var resolved = SharedVolumePath.Resolve(_base, "exports/deck.html");

        Assert.AreEqual(Path.Combine(_base, "exports", "deck.html"), resolved);
    }

    [TestMethod]
    public void AbsolutePathInsideTheBase_IsAccepted_AndAFileNeedNotExist()
    {
        var full = Path.Combine(_base, "tmp", "screenshots", "new.png");

        Assert.AreEqual(full, SharedVolumePath.Resolve(_base, full));
    }

    [TestMethod]
    public void TrailingSeparatorOnTheBase_MakesNoDifference()
    {
        Assert.IsNotNull(SharedVolumePath.Resolve(_base + Path.DirectorySeparatorChar, "exports/deck.html"));
    }

    [TestMethod]
    [DataRow("../outside.html")]
    [DataRow("exports/../../outside.html")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void EscapesAndEmptyPaths_AreRejected(string? path)
    {
        Assert.IsNull(SharedVolumePath.Resolve(_base, path));
    }

    [TestMethod]
    public void TheBaseItself_IsNotAFileInsideIt()
    {
        Assert.IsNull(SharedVolumePath.Resolve(_base, "."));
    }

    [TestMethod]
    public void SiblingDirectoryWithTheSamePrefix_IsRejected()
    {
        // "/shared-other/x" starts with "/shared" as a string but is not inside it.
        var sibling = _base + "-other";

        Assert.IsNull(SharedVolumePath.Resolve(_base, Path.Combine(sibling, "x.html")));
        Assert.IsFalse(SharedVolumePath.IsUnder(_base, Path.Combine(sibling, "x.html")));
    }

    [TestMethod]
    public void LinkedFile_IsRejected()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_base)!, "secret.txt");
        File.WriteAllText(outside, "token");
        Link(() => File.CreateSymbolicLink(Path.Combine(_base, "exports", "x.html"), outside));

        Assert.IsNull(SharedVolumePath.Resolve(_base, "exports/x.html"));
    }

    [TestMethod]
    public void FileBelowALinkedDirectory_IsRejected()
    {
        var outsideDir = Path.Combine(Path.GetDirectoryName(_base)!, "agent-data");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "mcp.html"), "secrets");
        Link(() => Directory.CreateSymbolicLink(Path.Combine(_base, "linked"), outsideDir));

        // The name is inside the volume; the file is not.
        Assert.IsNull(SharedVolumePath.Resolve(_base, "linked/mcp.html"));
        Assert.IsNull(SharedVolumePath.Resolve(_base, "linked/new/capture.png"), "nor may a new file be written through it");
    }

    [TestMethod]
    public void DanglingLink_IsRejected()
    {
        // A write through it would create the file wherever it points.
        Link(() => File.CreateSymbolicLink(
            Path.Combine(_base, "exports", "out.png"), Path.Combine(Path.GetDirectoryName(_base)!, "not-there.png")));

        Assert.IsNull(SharedVolumePath.Resolve(_base, "exports/out.png"));
    }

    [TestMethod]
    public void OrdinaryNestedFile_PassesThroughNoLink()
    {
        var file = Path.Combine(_base, "exports", "deck.html");
        File.WriteAllText(file, "<html/>");

        Assert.IsFalse(SharedVolumePath.PassesThroughLink(_base, file));
    }

    /// <summary>Creating links needs a privilege Windows does not grant by default; CI runs on Linux.</summary>
    private static void Link(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Cannot create symbolic links here: {ex.Message}");
        }
    }
}
