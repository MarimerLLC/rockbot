using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileAgentProfileProviderTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "agent"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task LoadAsync_WithAllFiles_ReturnsCompleteProfile()
    {
        WriteFile("agent/soul.md", "# My Soul\n\n## Identity\n\nI am helpful.");
        WriteFile("agent/directives.md", "## Goal\n\nBe useful.");
        WriteFile("agent/style.md", "## Tone\n\nFriendly.");

        var provider = CreateProvider();
        var profile = await provider.LoadAsync();

        Assert.IsNotNull(profile.Soul);
        Assert.AreEqual("soul", profile.Soul.DocumentType);
        Assert.AreEqual(1, profile.Soul.Sections.Count);
        Assert.AreEqual("Identity", profile.Soul.Sections[0].Name);

        Assert.IsNotNull(profile.Directives);
        Assert.AreEqual("directives", profile.Directives.DocumentType);

        Assert.IsNotNull(profile.Style);
        Assert.AreEqual("style", profile.Style.DocumentType);
    }

    [TestMethod]
    public async Task LoadAsync_MissingOptionalStyle_ReturnsProfileWithNullStyle()
    {
        WriteFile("agent/soul.md", "## Identity\n\nI am helpful.");
        WriteFile("agent/directives.md", "## Goal\n\nBe useful.");

        var provider = CreateProvider();
        var profile = await provider.LoadAsync();

        Assert.IsNotNull(profile.Soul);
        Assert.IsNotNull(profile.Directives);
        Assert.IsNull(profile.Style);
        Assert.AreEqual(2, profile.Documents.Count);
    }

    [TestMethod]
    public async Task LoadAsync_MissingSoul_ThrowsFileNotFoundException()
    {
        WriteFile("agent/directives.md", "## Goal\n\nBe useful.");

        var provider = CreateProvider();

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => provider.LoadAsync());
        Assert.IsTrue(ex.Message.Contains("soul"));
    }

    [TestMethod]
    public async Task LoadAsync_MissingDirectives_ThrowsFileNotFoundException()
    {
        WriteFile("agent/soul.md", "## Identity\n\nI am helpful.");

        var provider = CreateProvider();

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => provider.LoadAsync());
        Assert.IsTrue(ex.Message.Contains("directives"));
    }

    [TestMethod]
    public async Task LoadAsync_CustomBasePath_ResolvesCorrectly()
    {
        var customDir = Path.Combine(_tempDir, "custom");
        Directory.CreateDirectory(customDir);
        File.WriteAllText(Path.Combine(customDir, "soul.md"), "## Identity\n\nCustom soul.");
        File.WriteAllText(Path.Combine(customDir, "directives.md"), "## Goal\n\nCustom directives.");

        var provider = CreateProvider(opts => opts.BasePath = customDir);
        var profile = await provider.LoadAsync();

        Assert.IsTrue(profile.Soul.RawContent.Contains("Custom soul."));
    }

    [TestMethod]
    public async Task LoadAsync_AbsolutePaths_UsedDirectly()
    {
        var absDir = Path.Combine(_tempDir, "absolute");
        Directory.CreateDirectory(absDir);
        File.WriteAllText(Path.Combine(absDir, "my-soul.md"), "## Identity\n\nAbsolute soul.");
        File.WriteAllText(Path.Combine(absDir, "my-directives.md"), "## Goal\n\nAbsolute directives.");

        var provider = CreateProvider(opts =>
        {
            opts.SoulPath = Path.Combine(absDir, "my-soul.md");
            opts.DirectivesPath = Path.Combine(absDir, "my-directives.md");
            opts.StylePath = null;
        });

        var profile = await provider.LoadAsync();

        Assert.IsTrue(profile.Soul.RawContent.Contains("Absolute soul."));
    }

    [TestMethod]
    public async Task LoadAsync_StylePathNull_SkipsStyleEntirely()
    {
        WriteFile("agent/soul.md", "## Identity\n\nSoul.");
        WriteFile("agent/directives.md", "## Goal\n\nDirectives.");
        WriteFile("agent/style.md", "## Tone\n\nStyle exists but should be skipped.");

        var provider = CreateProvider(opts => opts.StylePath = null);
        var profile = await provider.LoadAsync();

        Assert.IsNull(profile.Style);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private FileAgentProfileProvider CreateProvider(Action<AgentProfileOptions>? configure = null)
    {
        // Use absolute BasePath so tests don't depend on AppContext.BaseDirectory
        var opts = new AgentProfileOptions
        {
            BasePath = Path.Combine(_tempDir, "agent")
        };
        configure?.Invoke(opts);

        return new FileAgentProfileProvider(
            Options.Create(opts),
            NullLogger<FileAgentProfileProvider>.Instance);
    }
}
