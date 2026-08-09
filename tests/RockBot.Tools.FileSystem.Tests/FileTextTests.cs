using System.Text;

namespace RockBot.Tools.FileSystem.Tests;

[TestClass]
public class FileTextTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-file-text-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Path_(string name) => Path.Combine(_root, name);

    /// <summary>
    /// Reads the file, applies a replacement, writes it back — the exact cycle
    /// <see cref="FileEditToolExecutor"/> performs.
    /// </summary>
    private static async Task<byte[]> RoundTripAsync(string path, string oldText, string newText)
    {
        var read = await FileText.ReadAsync(path, CancellationToken.None);
        Assert.IsTrue(read.IsSuccess, read.Error);
        await FileText.WriteAtomicAsync(
            path,
            read.Content!.Replace(oldText, newText, StringComparison.Ordinal),
            read.Encoding!,
            CancellationToken.None);
        return await File.ReadAllBytesAsync(path);
    }

    [TestMethod]
    public async Task RoundTrip_PreservesUtf8WithoutBom()
    {
        var path = Path_("plain.md");
        await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes("alpha — beta\n"));

        var bytes = await RoundTripAsync(path, "beta", "gamma");

        CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes("alpha — gamma\n"), bytes);
        Assert.AreNotEqual(0xEF, bytes[0], "a BOM must not be introduced");
    }

    [TestMethod]
    public async Task RoundTrip_PreservesUtf8Bom()
    {
        var path = Path_("bom.md");
        await File.WriteAllBytesAsync(path, new UTF8Encoding(true).GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes("alpha beta\n")).ToArray());

        var bytes = await RoundTripAsync(path, "beta", "gamma");

        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.AreEqual("alpha gamma\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [TestMethod]
    public async Task RoundTrip_PreservesUtf16LittleEndian()
    {
        // The shape a Windows PowerShell 5.1 producer leaves on the shared volume.
        var path = Path_("utf16.md");
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        await File.WriteAllBytesAsync(path, encoding.GetPreamble()
            .Concat(encoding.GetBytes("alpha — beta\n")).ToArray());

        var bytes = await RoundTripAsync(path, "beta", "gamma");

        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFE }, bytes.Take(2).ToArray());
        Assert.AreEqual("alpha — gamma\n", encoding.GetString(bytes, 2, bytes.Length - 2));
    }

    [TestMethod]
    public async Task RoundTrip_PreservesUtf16BigEndian()
    {
        var path = Path_("utf16be.md");
        var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        await File.WriteAllBytesAsync(path, encoding.GetPreamble()
            .Concat(encoding.GetBytes("alpha beta\n")).ToArray());

        var bytes = await RoundTripAsync(path, "beta", "gamma");

        CollectionAssert.AreEqual(new byte[] { 0xFE, 0xFF }, bytes.Take(2).ToArray());
        Assert.AreEqual("alpha gamma\n", encoding.GetString(bytes, 2, bytes.Length - 2));
    }

    [TestMethod]
    public async Task RoundTrip_PreservesUtf32LittleEndian()
    {
        // FF FE 00 00 must be read as UTF-32, not as UTF-16LE followed by a NUL.
        var path = Path_("utf32.md");
        var encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        await File.WriteAllBytesAsync(path, encoding.GetPreamble()
            .Concat(encoding.GetBytes("alpha beta\n")).ToArray());

        var bytes = await RoundTripAsync(path, "beta", "gamma");

        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, bytes.Take(4).ToArray());
        Assert.AreEqual("alpha gamma\n", encoding.GetString(bytes, 4, bytes.Length - 4));
    }

    [TestMethod]
    public async Task ReadAsync_RefusesUndecodableBytes_RatherThanSubstituting()
    {
        // Latin-1 "café" — 0xE9 is not valid UTF-8 and there is no BOM to disambiguate.
        var path = Path_("latin1.md");
        await File.WriteAllBytesAsync(path, [0x63, 0x61, 0x66, 0xE9, 0x0A]);

        var read = await FileText.ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(read.IsSuccess);
        StringAssert.Contains(read.Error!, "not valid UTF-8");
        CollectionAssert.AreEqual(
            new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A },
            await File.ReadAllBytesAsync(path),
            "a refused read must leave the file alone");
    }

    [TestMethod]
    public async Task WriteAtomicAsync_LeavesNoTempFileBehind()
    {
        var path = Path_("doc.md");
        await File.WriteAllTextAsync(path, "body\n");

        await RoundTripAsync(path, "body", "text");

        CollectionAssert.AreEqual(
            new[] { "doc.md" },
            Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task WriteAtomicAsync_PreservesOriginal_WhenCancelled()
    {
        var path = Path_("doc.md");
        const string original = "durable content\n";
        await File.WriteAllTextAsync(path, original);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            FileText.WriteAtomicAsync(path, "replacement", new UTF8Encoding(false), cts.Token));

        Assert.AreEqual(original, await File.ReadAllTextAsync(path),
            "a cancelled write must not truncate the file it was replacing");
        CollectionAssert.AreEqual(
            new[] { "doc.md" },
            Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task WriteAtomicIfUnchangedAsync_RefusesWrite_WhenFileChangedSinceRead()
    {
        var path = Path_("doc.md");
        await File.WriteAllTextAsync(path, "original\n");

        var read = await FileText.ReadAsync(path, CancellationToken.None);
        Assert.IsTrue(read.IsSuccess);

        // Another writer lands between the read and the write.
        await File.WriteAllTextAsync(path, "someone else's change\n");

        var written = await FileText.WriteAtomicIfUnchangedAsync(
            path, read.Bytes!, "my edit\n", read.Encoding!, CancellationToken.None);

        Assert.IsFalse(written);
        Assert.AreEqual("someone else's change\n", await File.ReadAllTextAsync(path),
            "the other writer's change must survive");
    }

    [TestMethod]
    public async Task WriteAtomicIfUnchangedAsync_Writes_WhenFileIsUntouched()
    {
        var path = Path_("doc.md");
        await File.WriteAllTextAsync(path, "original\n");

        var read = await FileText.ReadAsync(path, CancellationToken.None);
        var written = await FileText.WriteAtomicIfUnchangedAsync(
            path, read.Bytes!, "my edit\n", read.Encoding!, CancellationToken.None);

        Assert.IsTrue(written);
        Assert.AreEqual("my edit\n", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task WriteAtomicAsync_PreservesUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Unix file modes are not meaningful on Windows.");

        var path = Path_("doc.md");
        await File.WriteAllTextAsync(path, "body\n");
        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(path, mode);

        await RoundTripAsync(path, "body", "text");

        Assert.AreEqual(mode, File.GetUnixFileMode(path),
            "editing a file must not change its permissions");
    }
}
