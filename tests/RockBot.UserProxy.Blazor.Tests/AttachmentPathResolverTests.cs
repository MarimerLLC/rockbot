using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

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
}
