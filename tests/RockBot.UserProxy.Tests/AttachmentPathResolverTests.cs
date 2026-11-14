namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class AttachmentPathResolverTests
{
    private string _baseDir = null!;
    private AttachmentPathResolver _resolver = null!;

    [TestInitialize]
    public void Init()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "rb-resolver-tests", Guid.NewGuid().ToString("N"), "attachments");
        Directory.CreateDirectory(_baseDir);
        _resolver = new AttachmentPathResolver(_baseDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(Path.GetDirectoryName(_baseDir)!, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string name)
    {
        var full = Path.Combine(_baseDir, name);
        File.WriteAllBytes(full, [1, 2, 3]);
        return full;
    }

    [TestMethod]
    public void Resolve_ValidFile_ReturnsAbsolutePath()
    {
        var full = WriteFile("chart.png");

        var resolved = _resolver.Resolve("chart.png");

        Assert.AreEqual(Path.GetFullPath(full), resolved);
    }

    [TestMethod]
    public void Resolve_Null_ReturnsNull()
        => Assert.IsNull(_resolver.Resolve(null));

    [TestMethod]
    public void Resolve_Empty_ReturnsNull()
        => Assert.IsNull(_resolver.Resolve("   "));

    [TestMethod]
    public void Resolve_MissingFile_ReturnsNull()
        => Assert.IsNull(_resolver.Resolve("nope.png"));

    [TestMethod]
    public void Resolve_ParentTraversal_ReturnsNull()
    {
        // Even if a file exists one level up, traversal must be rejected.
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(_baseDir)!, "secret.png"), [9]);

        Assert.IsNull(_resolver.Resolve("../secret.png"));
    }

    [TestMethod]
    public void Resolve_AbsolutePathOutsideBase_ReturnsNull()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"rb-outside-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(outside, [1]);
        try
        {
            Assert.IsNull(_resolver.Resolve(outside));
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void Resolve_RedundantAttachmentsLeaf_ResolvesToSingleLayer()
    {
        var full = WriteFile("chart.png");

        var resolved = _resolver.Resolve("attachments/chart.png");

        Assert.AreEqual(Path.GetFullPath(full), resolved);
    }

    [TestMethod]
    public void GuessMime_KnownExtensions()
    {
        Assert.AreEqual("image/png", AttachmentPathResolver.GuessMime("a.png"));
        Assert.AreEqual("image/jpeg", AttachmentPathResolver.GuessMime("a.JPG"));
        Assert.AreEqual("application/pdf", AttachmentPathResolver.GuessMime("a.pdf"));
        Assert.AreEqual("application/octet-stream", AttachmentPathResolver.GuessMime("a.unknown"));
    }

    [TestMethod]
    [DataRow("chart.png")]
    [DataRow("previews/abc12345/deck.html")]
    [DataRow("attachments/chart.png")]
    [DataRow("my chart (2).png")]
    [DataRow("..chart.png", DisplayName = "dots in a name are not a traversal")]
    public void IsSafeRelativePath_AcceptsWhatAnUploadReturns(string path)
        => Assert.IsTrue(AttachmentPathResolver.IsSafeRelativePath(path));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("../secret.png")]
    [DataRow("previews/../../secret.png")]
    [DataRow("previews/..")]
    [DataRow("./chart.png")]
    [DataRow("/etc/passwd")]
    [DataRow("//server/share/x.png")]
    [DataRow("previews//deck.html")]
    [DataRow("previews/")]
    [DataRow(@"..\secret.png")]
    [DataRow(@"previews\deck.html")]
    [DataRow(@"C:\Windows\win.ini")]
    [DataRow("C:/Windows/win.ini")]
    [DataRow("chart.png:stream")]
    [DataRow("chart.png\0.html")]
    [DataRow("chart\n.png")]
    [DataRow(" chart.png")]
    [DataRow("previews/ ../x.png")]
    public void IsSafeRelativePath_RefusesWhatCouldLeaveTheDirectory(string? path)
        => Assert.IsFalse(AttachmentPathResolver.IsSafeRelativePath(path));

    [TestMethod]
    public void IsSafeRelativePath_RefusesAPathTooLongToBeAFileName()
        => Assert.IsFalse(AttachmentPathResolver.IsSafeRelativePath(new string('a', AttachmentPathResolver.MaxRelativePathLength + 1)));

    [TestMethod]
    public void Resolve_OfASafeRelativePath_StaysInsideTheBase()
    {
        // The lexical check and the resolver agree: what the first lets through, the second
        // resolves under the base (when the file is there).
        Directory.CreateDirectory(Path.Combine(_baseDir, "previews", "abc"));
        File.WriteAllBytes(Path.Combine(_baseDir, "previews", "abc", "deck.html"), [1]);

        Assert.IsTrue(AttachmentPathResolver.IsSafeRelativePath("previews/abc/deck.html"));
        var resolved = _resolver.Resolve("previews/abc/deck.html");

        Assert.IsNotNull(resolved);
        StringAssert.StartsWith(resolved, _baseDir);
    }
}