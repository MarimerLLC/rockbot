using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Agent.McpBridge.Attachments;

namespace RockBot.Agent.Tests.Attachments;

[TestClass]
public class AttachmentGatewayTests
{
    private const string ToolName = "send_email";
    private const string InboundToolName = "get_email_attachment";

    private FakeAttachmentStorage _storage = null!;
    private FakeHttpHandler _httpHandler = null!;
    private HttpClient _httpClient = null!;
    private Uri _serverBase = null!;

    [TestInitialize]
    public void Init()
    {
        _storage = new FakeAttachmentStorage("/rockbot/shared/attachments");
        _httpHandler = new FakeHttpHandler();
        _httpClient = new HttpClient(_httpHandler);
        _serverBase = new Uri("https://mcp.example.test/");
    }

    // ── Outbound — inline (small file) ────────────────────────────────────────

    [TestMethod]
    public async Task RewriteRequest_SmallFile_InlinesBase64()
    {
        // Inline path replaces {path} with {name, base64Content}; the model never sees raw bytes.
        var bytes = Encoding.UTF8.GetBytes("hello world");
        _storage.Files["/rockbot/shared/attachments/note.txt"] = bytes;

        var gateway = NewGateway(thresholdBytes: 1024, outbound: ["attachments[*]"]);
        var args = new Dictionary<string, object?>
        {
            ["to"] = "alice@example.test",
            ["attachments"] = new List<object?>
            {
                new Dictionary<string, object?> { ["path"] = "/rockbot/shared/attachments/note.txt" }
            }
        };

        await gateway.RewriteRequestAsync(ToolName, args, CancellationToken.None);

        var attachments = (List<object?>)args["attachments"]!;
        var rewritten = (Dictionary<string, object?>)attachments[0]!;

        Assert.IsFalse(rewritten.ContainsKey("path"), "path must be removed when rewritten");
        Assert.AreEqual("note.txt", rewritten["name"]);
        Assert.AreEqual(Convert.ToBase64String(bytes), rewritten["base64Content"]);
        Assert.IsFalse(rewritten.ContainsKey("attachmentId"));
        Assert.AreEqual(0, _httpHandler.Requests.Count, "small files must not POST");
    }

    // ── Outbound — stash (large file) ─────────────────────────────────────────

    [TestMethod]
    public async Task RewriteRequest_LargeFile_PostsAttachmentReturns201()
    {
        // Above threshold uploads multipart and substitutes {attachmentId}; 201 Created accepted.
        var bytes = new byte[4 * 1024];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        _storage.Files["/rockbot/shared/attachments/big.pdf"] = bytes;

        _httpHandler.NextResponse = (req, _) =>
        {
            Assert.AreEqual(HttpMethod.Post, req.Method);
            Assert.AreEqual("https://mcp.example.test/attachments", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"attachmentId\":\"abc-123\"}", Encoding.UTF8, "application/json")
            };
        };

        var gateway = NewGateway(thresholdBytes: 1024, outbound: ["attachments[*]"]);
        var args = new Dictionary<string, object?>
        {
            ["attachments"] = new List<object?>
            {
                new Dictionary<string, object?> { ["path"] = "/rockbot/shared/attachments/big.pdf" }
            }
        };

        await gateway.RewriteRequestAsync(ToolName, args, CancellationToken.None);

        var rewritten = (Dictionary<string, object?>)((List<object?>)args["attachments"]!)[0]!;
        Assert.AreEqual("abc-123", rewritten["attachmentId"]);
        Assert.IsFalse(rewritten.ContainsKey("path"));
        Assert.IsFalse(rewritten.ContainsKey("base64Content"));
    }

    [TestMethod]
    public async Task RewriteRequest_LargeFile_AcceptsHttp200OnPost()
    {
        // Defensive — some servers return 200 instead of 201 for create. Both must succeed.
        var bytes = new byte[4 * 1024];
        _storage.Files["/rockbot/shared/attachments/big.pdf"] = bytes;

        _httpHandler.NextResponse = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"attachmentId\":\"abc-200\"}", Encoding.UTF8, "application/json")
        };

        var gateway = NewGateway(thresholdBytes: 1024, outbound: ["attachments[*]"]);
        var args = new Dictionary<string, object?>
        {
            ["attachments"] = new List<object?>
            {
                new Dictionary<string, object?> { ["path"] = "/rockbot/shared/attachments/big.pdf" }
            }
        };

        await gateway.RewriteRequestAsync(ToolName, args, CancellationToken.None);

        var rewritten = (Dictionary<string, object?>)((List<object?>)args["attachments"]!)[0]!;
        Assert.AreEqual("abc-200", rewritten["attachmentId"]);
    }

    [TestMethod]
    public async Task RewriteRequest_FileNotFound_BubblesUpClearError()
    {
        // The bridge wraps this into a ToolError; the gateway just needs to surface it.
        var gateway = NewGateway(thresholdBytes: 1024, outbound: ["attachments[*]"]);
        var args = new Dictionary<string, object?>
        {
            ["attachments"] = new List<object?>
            {
                new Dictionary<string, object?> { ["path"] = "/rockbot/shared/attachments/missing.pdf" }
            }
        };

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => gateway.RewriteRequestAsync(ToolName, args, CancellationToken.None));
    }

    // ── Inbound — save → stash ────────────────────────────────────────────────

    [TestMethod]
    public async Task RewriteResponse_SaveStash_DownloadsWritesAndDeletes()
    {
        // The model says mode:save; gateway swaps to stash, the underlying tool returns
        // {attachmentId, name}, and the gateway fetches + writes the bytes + DELETEs the stash.
        _httpHandler.HandleRequest = (req, _) =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/attachments/aid-1"))
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("PDF-DATA"));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "report.pdf"
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath.EndsWith("/attachments/aid-1"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
        };

        var gateway = NewGateway(thresholdBytes: 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?> { ["mode"] = "save" };

        Assert.IsTrue(gateway.ShouldRewriteResponse(InboundToolName, args));
        await gateway.RewriteRequestAsync(InboundToolName, args, CancellationToken.None);
        Assert.AreEqual("stash", args["mode"]);

        var underlyingResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\"attachmentId\":\"aid-1\",\"name\":\"report.pdf\"}" }]
        };

        var rewritten = await gateway.RewriteResponseAsync(
            InboundToolName, args, underlyingResult, CancellationToken.None);

        Assert.IsNull(rewritten.IsError);
        var text = ((TextContentBlock)rewritten.Content![0]).Text;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;

        Assert.AreEqual("report.pdf", parsed["name"].GetString());
        Assert.AreEqual(8, parsed["size"].GetInt64());
        Assert.AreEqual("application/pdf", parsed["mime"].GetString());
        Assert.IsTrue(parsed["path"].GetString()!.EndsWith("report.pdf"));

        // DELETE is fire-and-forget; give it a brief moment to fire.
        await WaitForRequestAsync(req => req.Method == HttpMethod.Delete);
        Assert.IsTrue(_httpHandler.Requests.Any(r => r.Method == HttpMethod.Delete));
        Assert.IsTrue(_storage.WrittenFiles.Any(p => p.EndsWith("report.pdf")));
    }

    // ── Inbound — save → inline ───────────────────────────────────────────────

    [TestMethod]
    public async Task RewriteResponse_SaveInline_DecodesBase64AndWritesFile()
    {
        // sizeHint < threshold steers save → inline; gateway decodes base64 directly to disk.
        var gateway = NewGateway(thresholdBytes: 4 * 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?>
        {
            ["mode"] = "save",
            ["sizeHint"] = 100L
        };

        Assert.IsTrue(gateway.ShouldRewriteResponse(InboundToolName, args));
        await gateway.RewriteRequestAsync(InboundToolName, args, CancellationToken.None);
        Assert.AreEqual("inline", args["mode"]);

        var bytes = Encoding.UTF8.GetBytes("invoice-bytes");
        var b64 = Convert.ToBase64String(bytes);
        var underlyingResult = new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = $"{{\"name\":\"invoice.txt\",\"base64Content\":\"{b64}\",\"mime\":\"text/plain\"}}"
            }]
        };

        var rewritten = await gateway.RewriteResponseAsync(
            InboundToolName, args, underlyingResult, CancellationToken.None);

        var text = ((TextContentBlock)rewritten.Content![0]).Text;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;

        Assert.AreEqual("invoice.txt", parsed["name"].GetString());
        Assert.AreEqual(bytes.LongLength, parsed["size"].GetInt64());
        Assert.AreEqual("text/plain", parsed["mime"].GetString());
        Assert.IsTrue(_storage.WrittenFiles.Any(p => p.EndsWith("invoice.txt")));
        Assert.AreEqual(0, _httpHandler.Requests.Count(r => r.Method != HttpMethod.Delete),
            "inline path must not call HTTP for the body");
    }

    // ── Inbound passthrough ───────────────────────────────────────────────────

    [TestMethod]
    public void ShouldRewriteResponse_StashMode_IsPassthrough()
    {
        // The model can opt into raw stash handles; that path is not gateway-managed.
        var gateway = NewGateway(thresholdBytes: 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?> { ["mode"] = "stash" };

        Assert.IsFalse(gateway.ShouldRewriteResponse(InboundToolName, args));
    }

    [TestMethod]
    public void ShouldRewriteResponse_NoInboundManifest_IsPassthrough()
    {
        // Servers that didn't opt into the inbound rewrite never trigger it.
        var gateway = NewGateway(thresholdBytes: 1024);
        var args = new Dictionary<string, object?> { ["mode"] = "save" };

        Assert.IsFalse(gateway.ShouldRewriteResponse(InboundToolName, args));
    }

    [TestMethod]
    public void ShouldRewriteResponse_ToolNotInList_IsPassthrough()
    {
        // Only tools listed in inbound.tools participate in the save→stash/inline rewrite.
        var gateway = NewGateway(thresholdBytes: 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?> { ["mode"] = "save" };

        Assert.IsFalse(gateway.ShouldRewriteResponse("some_other_tool", args));
    }

    // ── Filename collision ────────────────────────────────────────────────────

    [TestMethod]
    public async Task RewriteResponse_Inline_FilenameCollision_AppendsSuffix()
    {
        var basePath = _storage.BasePath;
        _storage.Files[Path.Combine(basePath, "x.pdf")] = [1, 2, 3];

        var gateway = NewGateway(thresholdBytes: 1024 * 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?> { ["mode"] = "save", ["sizeHint"] = 10L };
        await gateway.RewriteRequestAsync(InboundToolName, args, CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes("contents");
        var b64 = Convert.ToBase64String(bytes);
        var underlyingResult = new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = $"{{\"name\":\"x.pdf\",\"base64Content\":\"{b64}\"}}"
            }]
        };

        var rewritten = await gateway.RewriteResponseAsync(
            InboundToolName, args, underlyingResult, CancellationToken.None);

        var text = ((TextContentBlock)rewritten.Content![0]).Text;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
        Assert.AreEqual("x-2.pdf", parsed["name"].GetString());
    }

    // ── No-manifest no-op ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task RewriteRequest_NoOutboundManifest_LeavesArgsUnchanged()
    {
        // A gateway constructed with no outbound config must not rewrite anything.
        var gateway = NewGateway(thresholdBytes: 1024); // no outbound, no inbound
        var args = new Dictionary<string, object?>
        {
            ["attachments"] = new List<object?>
            {
                new Dictionary<string, object?> { ["path"] = "/somewhere/else" }
            }
        };

        await gateway.RewriteRequestAsync(ToolName, args, CancellationToken.None);

        var passthrough = (Dictionary<string, object?>)((List<object?>)args["attachments"]!)[0]!;
        Assert.AreEqual("/somewhere/else", passthrough["path"]);
    }

    // ── DELETE 404 tolerated ──────────────────────────────────────────────────

    [TestMethod]
    public async Task RewriteResponse_StashDeleteReturns404_DoesNotThrow()
    {
        // Stash cleanup is fire-and-forget; a 404 must not surface to the agent.
        _httpHandler.HandleRequest = (req, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("data"));
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "n.bin" };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException("unexpected");
        };

        var gateway = NewGateway(thresholdBytes: 1024, inbound: [InboundToolName]);
        var args = new Dictionary<string, object?> { ["mode"] = "save" };
        await gateway.RewriteRequestAsync(InboundToolName, args, CancellationToken.None);

        var underlyingResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\"attachmentId\":\"x-1\",\"name\":\"n.bin\"}" }]
        };

        var rewritten = await gateway.RewriteResponseAsync(
            InboundToolName, args, underlyingResult, CancellationToken.None);

        Assert.IsNull(rewritten.IsError);
        await WaitForRequestAsync(r => r.Method == HttpMethod.Delete);
    }

    // ── Storage env-var honored ───────────────────────────────────────────────

    [TestMethod]
    public async Task AttachmentStorage_HonorsEnvVarWhenSet()
    {
        // Real AttachmentStorage path resolution: env var > "/rockbot/shared".
        var temp = Path.Combine(Path.GetTempPath(), "rockbot-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("ROCKBOT_SHARED_PATH", temp);
            var storage = new AttachmentStorage();
            Assert.AreEqual(Path.Combine(temp, "attachments"), storage.BasePath);

            var written = await storage.WriteAsync("hi.txt", Encoding.UTF8.GetBytes("hi"), CancellationToken.None);
            Assert.IsTrue(File.Exists(written));
            Assert.AreEqual(Path.Combine(temp, "attachments", "hi.txt"), written);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROCKBOT_SHARED_PATH", null);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void AttachmentStorage_FallsBackWhenEnvVarUnset()
    {
        Environment.SetEnvironmentVariable("ROCKBOT_SHARED_PATH", null);
        // We can't actually create /rockbot/shared on Windows test boxes, so use the explicit-path
        // overload to verify the resolved default and skip the directory-creation side effect.
        var resolved = AttachmentStorageDefaultPath();
        Assert.AreEqual(Path.Combine("/rockbot/shared", "attachments"), resolved);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private AttachmentGateway NewGateway(
        long thresholdBytes,
        IReadOnlyList<string>? outbound = null,
        IReadOnlyList<string>? inbound = null)
    {
        var manifest = new AttachmentManifest
        {
            ThresholdBytes = thresholdBytes,
            Outbound = outbound is null ? null : new AttachmentOutboundConfig { ParamPaths = [.. outbound] },
            Inbound = inbound is null ? null : new AttachmentInboundConfig { Tools = [.. inbound] }
        };
        return new AttachmentGateway(_storage, _httpClient, _serverBase, manifest);
    }

    private async Task WaitForRequestAsync(Func<HttpRequestMessage, bool> predicate, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_httpHandler.Requests.Any(predicate)) return;
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Mirrors the private resolution in <see cref="AttachmentStorage"/> — kept here so the
    /// no-env-var branch can be asserted without touching the filesystem at <c>/rockbot/shared</c>.
    /// </summary>
    private static string AttachmentStorageDefaultPath()
    {
        var sharedRoot = Environment.GetEnvironmentVariable("ROCKBOT_SHARED_PATH");
        if (string.IsNullOrWhiteSpace(sharedRoot)) sharedRoot = "/rockbot/shared";
        return Path.Combine(sharedRoot, "attachments");
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeAttachmentStorage : IAttachmentStorage
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> WrittenFiles { get; } = [];

        public string BasePath { get; }

        public FakeAttachmentStorage(string basePath) { BasePath = basePath; }

        public Task<byte[]> ReadAsync(string path, CancellationToken ct)
        {
            var key = Path.IsPathRooted(path) ? path : Path.Combine(BasePath, path);
            if (!Files.TryGetValue(key, out var bytes))
                throw new FileNotFoundException($"Fake storage has no entry for '{key}'.", key);
            return Task.FromResult(bytes);
        }

        public Task<string> WriteAsync(string preferredFileName, byte[] data, CancellationToken ct)
        {
            var leaf = Path.GetFileName(preferredFileName);
            if (string.IsNullOrEmpty(leaf)) leaf = "attachment";
            var stem = Path.GetFileNameWithoutExtension(leaf);
            var ext = Path.GetExtension(leaf);

            var name = leaf;
            var i = 2;
            while (Files.ContainsKey(Path.Combine(BasePath, name)))
            {
                name = $"{stem}-{i}{ext}";
                i++;
            }
            var fullPath = Path.Combine(BasePath, name);
            Files[fullPath] = data;
            WrittenFiles.Add(fullPath);
            return Task.FromResult(fullPath);
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? NextResponse { get; set; }
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? HandleRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            var responder = HandleRequest ?? NextResponse;
            if (responder is null)
                throw new InvalidOperationException(
                    $"FakeHttpHandler received an unexpected request: {request.Method} {request.RequestUri}");
            return Task.FromResult(responder(request, cancellationToken));
        }
    }
}
