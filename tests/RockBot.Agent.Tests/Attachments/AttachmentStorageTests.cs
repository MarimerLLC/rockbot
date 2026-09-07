using RockBot.Agent.McpBridge.Attachments;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Containment tests for the write path.
/// </summary>
/// <remarks>
/// Binary capture names saved files from the <c>name</c> field of an MCP server's response,
/// which is to say from a remote party. That makes the leaf-only sanitising in
/// <see cref="AttachmentStorage"/> a security property rather than a tidiness one, and worth
/// pinning: a refactor that "simplified" it into a <c>Path.Combine</c> would let a hostile
/// server write anywhere the agent can reach.
/// </remarks>
[TestClass]
public class AttachmentStorageTests
{
    private string _root = null!;
    private AttachmentStorage _storage = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-attachment-storage-tests", Guid.NewGuid().ToString("N"));
        _storage = new AttachmentStorage(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [DataTestMethod]
    [DataRow("../escaped.png")]
    [DataRow("../../../../etc/passwd")]
    [DataRow("subdir/nested.png")]
    [DataRow("/etc/shadow")]
    public async Task WriteAsync_TraversalInName_StaysInsideTheBaseDirectory(string hostileName)
    {
        var written = await _storage.WriteAsync(hostileName, [1, 2, 3], CancellationToken.None);

        var basePath = Path.GetFullPath(_root);
        var parent = Path.GetDirectoryName(Path.GetFullPath(written));
        Assert.AreEqual(basePath, parent, $"'{hostileName}' escaped the attachments directory");
        Assert.IsTrue(File.Exists(written));
    }

    [TestMethod]
    public async Task WriteAsync_EmptyName_FallsBackToADefault()
    {
        var written = await _storage.WriteAsync("   ", [1], CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(_root), Path.GetDirectoryName(Path.GetFullPath(written)));
        Assert.IsTrue(File.Exists(written));
    }

    [TestMethod]
    public async Task WriteAsync_SameNameTwice_DoesNotOverwrite()
    {
        var first = await _storage.WriteAsync("chart.png", [1], CancellationToken.None);
        var second = await _storage.WriteAsync("chart.png", [2], CancellationToken.None);

        Assert.AreNotEqual(first, second);
        CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(first));
        CollectionAssert.AreEqual(new byte[] { 2 }, await File.ReadAllBytesAsync(second));
    }

    [TestMethod]
    public async Task ReadAsync_PathOutsideTheBase_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret");

        try
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
                () => _storage.ReadAsync(outside, CancellationToken.None));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
