using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Tools.Web.Tests;

/// <summary><c>render_screenshot</c> (#669): the agent's way to see a deck it wrote.</summary>
[TestClass]
public class RenderScreenshotToolExecutorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private string _root = null!;
    private WebToolOptions _options = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rb-screenshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "exports"));
        _options = new WebToolOptions
        {
            RenderServiceUrl = "http://localhost:8083/",
            SharedBasePath = _root,
            RenderTimeoutSeconds = 30
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relativePath, string content = "<html><body><h1>Title</h1></body></html>") =>
        File.WriteAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);

    private RenderScreenshotToolExecutor Executor(HttpMessageHandler handler) =>
        new(new StubFactory(handler), _options, new FixedTime(new DateTimeOffset(2026, 11, 13, 10, 15, 0, TimeSpan.Zero)));

    private static ToolInvokeRequest Request(object args) => new()
    {
        ToolCallId = "call_1",
        ToolName = "render_screenshot",
        Arguments = JsonSerializer.Serialize(args)
    };

    [TestMethod]
    public async Task PostsTheFileToTheService_AndSavesThePng()
    {
        Write("exports/deck.html", "<html>the deck</html>");
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("http://localhost:8083/screenshot", service.LastUri);
        using var body = JsonDocument.Parse(service.LastBody!);
        Assert.AreEqual("<html>the deck</html>", body.RootElement.GetProperty("html").GetString());
        Assert.AreEqual(30_000, body.RootElement.GetProperty("timeoutMs").GetInt32());

        // Default output: tmp/, which is swept after a day, named after the source and the time.
        var saved = Path.Combine(_root, "tmp", "screenshots", "deck-20261113-101500.png");
        CollectionAssert.AreEqual(Png, File.ReadAllBytes(saved));
        StringAssert.Contains(response.Content, "tmp/screenshots/deck-20261113-101500.png");
    }

    [TestMethod]
    public async Task Result_TellsTheAgentItHasNotSeenTheImageYet()
    {
        Write("exports/deck.html");

        var response = await Executor(new ScreenshotStub(() => PngResponse()))
            .ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        // A saved path is not a look at the slide; the next step has to be named.
        StringAssert.Contains(response.Content, "analyze_file");
        StringAssert.Contains(response.Content, "not seen");
    }

    [TestMethod]
    public async Task ViewportAndOutputPath_AreHonoured()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(
            Request(new { path = "exports/deck.html", output_path = "drafts/shots/first.png", width = 1920, height = "1080" }),
            CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        using var body = JsonDocument.Parse(service.LastBody!);
        Assert.AreEqual(1920, body.RootElement.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, body.RootElement.GetProperty("height").GetInt32(), "a number sent as a string still counts");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "drafts", "shots", "first.png")));
        StringAssert.Contains(response.Content, "drafts/shots/first.png");
    }

    [TestMethod]
    public async Task BlockedRequests_AreReported()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() =>
        {
            var ok = PngResponse();
            ok.Headers.Add(RenderScreenshotToolExecutor.BlockedRequestsHeader, "2");
            return ok;
        });

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "2 request(s)");
    }

    [TestMethod]
    public async Task ServiceError_IsSurfaced_AndNothingIsWritten()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"error":"Timeout 30000ms exceeded."}""", Encoding.UTF8, "application/json")
        });

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Timeout 30000ms exceeded.");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "tmp")));
    }

    [TestMethod]
    public async Task ServiceUnreachable_IsAnError_NotAnException()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => throw new HttpRequestException("Connection refused"));

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "Connection refused");
    }

    [TestMethod]
    [DataRow("../outside.html")]
    [DataRow("exports/../../outside.html")]
    public async Task SourceOutsideTheSharedVolume_IsRejected_WithoutCallingTheService(string path)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside.html"), "<html/>");
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(Request(new { path }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "within the shared volume");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public async Task OutputOutsideTheSharedVolume_IsRejected_WithoutCallingTheService()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(
            Request(new { path = "exports/deck.html", output_path = "../escaped.png" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "output_path");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public async Task OutputThatIsNotAPng_IsRejected()
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => PngResponse());

        // Must not be a way to overwrite the deck, or any other file, with image bytes.
        var response = await Executor(service).ExecuteAsync(
            Request(new { path = "exports/deck.html", output_path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, ".png");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public async Task ExistingOutputFile_IsNeverOverwritten()
    {
        Write("exports/deck.html");
        Write("exports/q3-chart.png", "the user's chart");
        var service = new ScreenshotStub(() => PngResponse());

        // file_write would refuse or keep a backup; this tool has neither, so it must not replace.
        var response = await Executor(service).ExecuteAsync(
            Request(new { path = "exports/deck.html", output_path = "exports/q3-chart.png" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "already exists");
        Assert.AreEqual(0, service.Calls);
        Assert.AreEqual("the user's chart", File.ReadAllText(Path.Combine(_root, "exports", "q3-chart.png")));
    }

    [TestMethod]
    public async Task DefaultName_StepsAside_ForACaptureMadeInTheSameSecond()
    {
        Write("exports/deck.html");
        var executor = Executor(new ScreenshotStub(() => PngResponse()));

        var first = await executor.ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);
        var second = await executor.ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsFalse(first.IsError, first.Content);
        Assert.IsFalse(second.IsError, second.Content);
        StringAssert.Contains(first.Content, "tmp/screenshots/deck-20261113-101500.png");
        StringAssert.Contains(second.Content, "tmp/screenshots/deck-20261113-101500-2.png");
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(_root, "tmp", "screenshots")).Length);
    }

    [TestMethod]
    [DataRow("1280.0", "720.0", 1280, 720)]   // float-typed numbers, as some models send them
    [DataRow("1e3", "5e2", 1000, 500)]
    [DataRow("\"1920\"", "\"1080.0\"", 1920, 1080)]
    [DataRow("1e30", "-1e30", int.MaxValue, int.MinValue)] // absurd, but the service clamps; do not throw
    public async Task ViewportNumbers_InAnyJsonForm_AreAccepted(string width, string height, int expectedWidth, int expectedHeight)
    {
        Write("exports/deck.html");
        var service = new ScreenshotStub(() => PngResponse());
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "render_screenshot",
            Arguments = $$"""{"path":"exports/deck.html","width":{{width}},"height":{{height}}}"""
        };

        var response = await Executor(service).ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        using var body = JsonDocument.Parse(service.LastBody!);
        Assert.AreEqual(expectedWidth, body.RootElement.GetProperty("width").GetInt32());
        Assert.AreEqual(expectedHeight, body.RootElement.GetProperty("height").GetInt32());
    }

    [TestMethod]
    public async Task SymbolicLinkSource_IsRejected_WithoutCallingTheService()
    {
        // A link planted on the shared volume must not get the agent's own files posted and pictured.
        var secret = Path.Combine(Path.GetDirectoryName(_root)!, $"secret-{Guid.NewGuid():N}.html");
        File.WriteAllText(secret, "token");
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(_root, "exports", "x.html"), secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive($"Cannot create symbolic links here: {ex.Message}");
            }
            var service = new ScreenshotStub(() => PngResponse());

            var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/x.html" }), CancellationToken.None);

            Assert.IsTrue(response.IsError);
            StringAssert.Contains(response.Content, "symbolic link");
            Assert.AreEqual(0, service.Calls);
        }
        finally
        {
            try { File.Delete(secret); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task NonHtmlSource_IsRejected_AndPointsAtAnalyzeFile()
    {
        File.WriteAllText(Path.Combine(_root, "exports", "deck.md"), "# Slide");
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.md" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "not an HTML file");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public async Task MissingFile_And_MissingPath_AreErrors()
    {
        var service = new ScreenshotStub(() => PngResponse());
        var executor = Executor(service);

        var missingFile = await executor.ExecuteAsync(Request(new { path = "exports/nope.html" }), CancellationToken.None);
        var missingPath = await executor.ExecuteAsync(Request(new { }), CancellationToken.None);

        Assert.IsTrue(missingFile.IsError);
        StringAssert.Contains(missingFile.Content, "File not found");
        Assert.IsTrue(missingPath.IsError);
        StringAssert.Contains(missingPath.Content, "path");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    public async Task OversizeSource_IsRejected()
    {
        Write("exports/deck.html");
        using (var stream = new FileStream(Path.Combine(_root, "exports", "deck.html"), FileMode.Open, FileAccess.Write))
            stream.SetLength(RenderScreenshotToolExecutor.MaxHtmlBytes + 1);
        var service = new ScreenshotStub(() => PngResponse());

        var response = await Executor(service).ExecuteAsync(Request(new { path = "exports/deck.html" }), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "limit");
        Assert.AreEqual(0, service.Calls);
    }

    [TestMethod]
    [DataRow("http://localhost:8083", true)]
    [DataRow(null, false)]
    [DataRow("", false)]
    public async Task Tool_IsRegistered_OnlyWithARenderService(string? renderServiceUrl, bool expected)
    {
        var registry = new RecordingRegistry();
        var options = new WebToolOptions { RenderServiceUrl = renderServiceUrl };
        var registrar = new WebToolRegistrar(
            registry, searchProvider: null!, browseProvider: null!, workingMemory: null!, options,
            new StubFactory(new ScreenshotStub(() => PngResponse())), NullLogger<WebToolRegistrar>.Instance);

        await registrar.StartAsync(CancellationToken.None);

        Assert.AreEqual(expected, registry.Names.Contains("render_screenshot"));
        CollectionAssert.Contains(registry.Names, "web_browse");
    }

    private static HttpResponseMessage PngResponse()
    {
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ScreenshotStub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond();
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRegistry : IToolRegistry
    {
        private readonly List<ToolRegistration> _tools = [];

        public List<string> Names => _tools.Select(t => t.Name).ToList();

        public IReadOnlyList<ToolRegistration> GetTools() => _tools;
        public void Register(ToolRegistration registration, IToolExecutor executor) => _tools.Add(registration);
        public bool Unregister(string toolName) => _tools.RemoveAll(t => t.Name == toolName) > 0;
        public IToolExecutor? GetExecutor(string toolName) => null;
    }
}
