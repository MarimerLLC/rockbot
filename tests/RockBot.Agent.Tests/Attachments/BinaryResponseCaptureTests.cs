using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.Attachments;

namespace RockBot.Agent.Tests.Attachments;

[TestClass]
public class BinaryResponseCaptureTests
{
    private const string Server = "gitea";
    private const string Tool = "get_file_contents";

    private FakeStorage _storage = null!;
    private BinaryResponseCapture _capture = null!;

    /// <summary>Smallest valid PNG — enough to carry a recognisable extension and real bytes.</summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// A content block's payload as it actually arrives: base64 text, despite the property being
    /// typed as bytes. Capture has to decode it, or a .png on disk contains "iVBORw0KGgo…".
    /// </summary>
    private static byte[] WireShape(byte[] payload) =>
        System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(payload));

    [TestInitialize]
    public void Init()
    {
        _storage = new FakeStorage("/rockbot/shared/attachments");
        _capture = new BinaryResponseCapture(_storage);
    }

    private static AttachmentCaptureConfig RuleConfig(
        string contentField = "content",
        string? nameField = "name",
        string? mimeField = null,
        string? encodingField = "encoding",
        params string[] tools) =>
        new()
        {
            Rules =
            [
                new AttachmentCaptureRule
                {
                    Tools = [.. tools.Length > 0 ? tools : [Tool]],
                    ContentField = contentField,
                    NameField = nameField,
                    MimeField = mimeField,
                    EncodingField = encodingField
                }
            ]
        };

    private static CallToolResult TextResult(object payload) =>
        new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }] };

    private static JsonElement ParseSingleText(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content!.Single()).Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ── Rule 1: typed content blocks, no configuration required ───────────────

    [TestMethod]
    public async Task Capture_ImageBlock_WritesFileAndReturnsPath()
    {
        var result = new CallToolResult
        {
            Content = [new ImageContentBlock { Data = WireShape(PngBytes), MimeType = "image/png" }]
        };

        var captured = await _capture.CaptureAsync(Server, "read_image", result, null, default);

        var payload = ParseSingleText(captured);
        var path = payload.GetProperty("path").GetString()!;
        Assert.AreEqual(PngBytes.Length, payload.GetProperty("size").GetInt32());
        Assert.AreEqual("image/png", payload.GetProperty("mime").GetString());
        StringAssert.EndsWith(path, ".png");
        CollectionAssert.AreEqual(PngBytes, _storage.Files[path]);
    }

    [TestMethod]
    public async Task Capture_AudioBlock_IsCapturedToo()
    {
        var bytes = new byte[] { 0x49, 0x44, 0x33, 0x00, 0x01 };
        var result = new CallToolResult
        {
            Content = [new AudioContentBlock { Data = WireShape(bytes), MimeType = "audio/mpeg" }]
        };

        var captured = await _capture.CaptureAsync(Server, "read_audio", result, null, default);

        StringAssert.EndsWith(ParseSingleText(captured).GetProperty("path").GetString(), ".mp3");
    }

    [TestMethod]
    public async Task Capture_MixedBlocks_LeavesTextAlone()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "Here is the diagram:" },
                new ImageContentBlock { Data = WireShape(PngBytes), MimeType = "image/png" }
            ]
        };

        var captured = await _capture.CaptureAsync(Server, "read_image", result, null, default);

        Assert.AreEqual(2, captured.Content!.Count);
        Assert.AreEqual("Here is the diagram:", ((TextContentBlock)captured.Content[0]).Text);
        StringAssert.Contains(((TextContentBlock)captured.Content[1]).Text, "\"path\"");
    }

    [TestMethod]
    public async Task Capture_TextOnlyResult_IsUntouched()
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "plain answer" }] };

        var captured = await _capture.CaptureAsync(Server, "some_tool", result, null, default);

        Assert.AreSame(result, captured);
        Assert.AreEqual(0, _storage.Written.Count);
    }

    [TestMethod]
    public async Task Capture_Disabled_LeavesImageBlockAlone()
    {
        var result = new CallToolResult
        {
            Content = [new ImageContentBlock { Data = WireShape(PngBytes), MimeType = "image/png" }]
        };

        var captured = await _capture.CaptureAsync(
            Server, "read_image", result, new AttachmentCaptureConfig { Enabled = false }, default);

        Assert.AreSame(result, captured);
        Assert.AreEqual(0, _storage.Written.Count);
    }

    [TestMethod]
    public async Task Capture_ErrorResult_IsUntouched()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new ImageContentBlock { Data = WireShape(PngBytes), MimeType = "image/png" }]
        };

        Assert.AreSame(result, await _capture.CaptureAsync(Server, "read_image", result, null, default));
    }

    // ── Rule 2: declared base64 fields (the Gitea shape) ──────────────────────

    [TestMethod]
    public async Task Capture_DeclaredBase64Image_WritesFileAndStripsContent()
    {
        var result = TextResult(new
        {
            name = "architecture.png",
            path = "docs/architecture.png",
            sha = "abc123",
            encoding = "base64",
            content = Convert.ToBase64String(PngBytes)
        });

        var captured = await _capture.CaptureAsync(Server, Tool, result, RuleConfig(), default);

        var payload = ParseSingleText(captured);
        Assert.IsFalse(payload.TryGetProperty("content", out _), "the base64 payload must not survive");
        Assert.AreEqual("abc123", payload.GetProperty("sha").GetString(), "unrelated fields are preserved");
        Assert.AreEqual("image/png", payload.GetProperty("mime").GetString());
        Assert.AreEqual(PngBytes.Length, payload.GetProperty("size").GetInt32());
        CollectionAssert.AreEqual(PngBytes, _storage.Files[payload.GetProperty("path").GetString()!]);
    }

    [TestMethod]
    public async Task Capture_DeclaredBase64Markdown_IsLeftInTheResponse()
    {
        // The whole point of the binary test: a repository server returns text and images
        // through the same tool and the same field, and capturing a README to disk would take
        // away content the model could simply have read.
        var markdown = Encoding.UTF8.GetBytes("# README\n\nHello, world.\n");
        var result = TextResult(new
        {
            name = "README.md",
            encoding = "base64",
            content = Convert.ToBase64String(markdown)
        });

        var captured = await _capture.CaptureAsync(Server, Tool, result, RuleConfig(), default);

        Assert.AreSame(result, captured);
        Assert.AreEqual(0, _storage.Written.Count);
    }

    [TestMethod]
    public async Task Capture_UnnamedBinaryPayload_FallsBackToSniffing()
    {
        var result = TextResult(new { content = Convert.ToBase64String(PngBytes) });

        var captured = await _capture.CaptureAsync(
            Server, Tool, result, RuleConfig(nameField: null), default);

        Assert.AreEqual(1, _storage.Written.Count);
        CollectionAssert.AreEqual(PngBytes, _storage.Files[ParseSingleText(captured).GetProperty("path").GetString()!]);
    }

    [TestMethod]
    public async Task Capture_UnnamedTextPayload_FallsBackToSniffingAndDeclines()
    {
        var result = TextResult(new { content = Convert.ToBase64String("just some prose"u8.ToArray()) });

        var captured = await _capture.CaptureAsync(
            Server, Tool, result, RuleConfig(nameField: null), default);

        Assert.AreSame(result, captured);
    }

    [TestMethod]
    public async Task Capture_NonBase64Encoding_Declines()
    {
        var result = TextResult(new { name = "diagram.png", encoding = "utf-8", content = "not base64 at all" });

        Assert.AreSame(result, await _capture.CaptureAsync(Server, Tool, result, RuleConfig(), default));
    }

    [TestMethod]
    public async Task Capture_MalformedBase64_DeclinesWithoutFailing()
    {
        var result = TextResult(new { name = "diagram.png", encoding = "base64", content = "!!!not-base64!!!" });

        Assert.AreSame(result, await _capture.CaptureAsync(Server, Tool, result, RuleConfig(), default));
    }

    [TestMethod]
    public async Task Capture_BinaryMangledIntoText_DropsTheFieldAndExplains()
    {
        // Measured on a live deployment: a repository server returns a PNG through its file tool
        // as UTF-8-decoded text. The bytes are already destroyed; the only question is whether
        // 1.37M characters of mojibake also get to flood context.
        var mangled = new string('�', 40) + new string('x', 2000);
        var result = TextResult(new
        {
            name = "rockbot.png",
            sha = "511bdfe",
            size = 345864,
            content = mangled
        });

        var captured = await _capture.CaptureAsync(Server, Tool, result, RuleConfig(encodingField: null), default);

        var payload = ParseSingleText(captured);
        Assert.IsFalse(payload.TryGetProperty("content", out _));
        Assert.AreEqual("511bdfe", payload.GetProperty("sha").GetString(), "metadata is what's left worth having");
        StringAssert.Contains(payload.GetProperty("note").GetString()!, "corrupted");
        Assert.AreEqual(0, _storage.Written.Count, "there are no bytes to save");
    }

    [TestMethod]
    public async Task Capture_TextWithAFewEncodingGlitches_IsNotTreatedAsMangled()
    {
        // A document with a couple of bad characters is still a document.
        var text = "# Report\n\nThe caf� served cr�me br�l�e.\n" + new string('x', 2000);
        var result = TextResult(new { name = "notes.md", content = text });

        var captured = await _capture.CaptureAsync(Server, Tool, result, RuleConfig(encodingField: null), default);

        Assert.AreSame(result, captured);
    }

    [TestMethod]
    public async Task Capture_ShortNonBase64Content_IsLeftAlone()
    {
        var result = TextResult(new { name = "notes.md", content = "not base64 ���������" });

        Assert.AreSame(result, await _capture.CaptureAsync(Server, Tool, result, RuleConfig(encodingField: null), default));
    }

    [TestMethod]
    public async Task Capture_RuleForAnotherTool_DoesNotMatch()
    {
        var result = TextResult(new
        {
            name = "architecture.png",
            encoding = "base64",
            content = Convert.ToBase64String(PngBytes)
        });

        var captured = await _capture.CaptureAsync(
            Server, "some_other_tool", result, RuleConfig(), default);

        Assert.AreSame(result, captured);
    }

    [TestMethod]
    public async Task Capture_NoRules_LeavesJsonBase64Alone()
    {
        // Without a declared rule the gateway does not go looking for base64 — sniffing every
        // JSON field is the fragile heuristic the manifest design exists to avoid.
        var result = TextResult(new
        {
            name = "architecture.png",
            encoding = "base64",
            content = Convert.ToBase64String(PngBytes)
        });

        Assert.AreSame(result, await _capture.CaptureAsync(Server, Tool, result, null, default));
    }

    [TestMethod]
    public async Task Capture_MimeFieldOverridesExtension()
    {
        var result = TextResult(new
        {
            name = "download",
            mimeType = "application/pdf",
            encoding = "base64",
            content = Convert.ToBase64String(PngBytes)
        });

        var captured = await _capture.CaptureAsync(
            Server, Tool, result, RuleConfig(mimeField: "mimeType"), default);

        Assert.AreEqual("application/pdf", ParseSingleText(captured).GetProperty("mime").GetString());
    }

    [TestMethod]
    public async Task Capture_StorageFailure_ReturnsOriginalResult()
    {
        // Capture is an optimisation. A tool call that worked before must not start failing
        // because the shared volume is unwritable.
        var capture = new BinaryResponseCapture(new ThrowingStorage());
        var result = new CallToolResult
        {
            Content = [new ImageContentBlock { Data = WireShape(PngBytes), MimeType = "image/png" }]
        };

        Assert.AreSame(result, await capture.CaptureAsync(Server, "read_image", result, null, default));
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeStorage(string basePath) : IAttachmentStorage
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Written { get; } = [];
        public string BasePath { get; } = basePath;

        public Task<byte[]> ReadAsync(string path, CancellationToken ct) =>
            Task.FromResult(Files[path]);

        public Task<string> WriteAsync(string preferredFileName, byte[] data, CancellationToken ct)
        {
            var fullPath = Path.Combine(BasePath, Path.GetFileName(preferredFileName));
            Files[fullPath] = data;
            Written.Add(fullPath);
            return Task.FromResult(fullPath);
        }
    }

    private sealed class ThrowingStorage : IAttachmentStorage
    {
        public string BasePath => "/rockbot/shared/attachments";
        public Task<byte[]> ReadAsync(string path, CancellationToken ct) => throw new IOException("nope");
        public Task<string> WriteAsync(string preferredFileName, byte[] data, CancellationToken ct) =>
            throw new IOException("read-only file system");
    }
}
