using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Exercises the agent's HTTP upload endpoint against a real Kestrel listener on a real socket.
/// </summary>
/// <remarks>
/// This endpoint exists so a screenshot-sized file never has to cross the message bus — RabbitMQ
/// holds message bodies in memory until they are acked, and a file being moved onto a volume the
/// agent already mounts is not a message. The bus path stays as the fallback for clients that
/// cannot reach this port, so both have to work, and both have to enforce the same rules.
/// </remarks>
[TestClass]
public class AttachmentUploadEndpointTests
{
    private string _root = null!;
    private AttachmentUploadEndpoint _endpoint = null!;
    private CancellationTokenSource _cts = null!;
    private HttpClient _client = null!;
    private int _port;

    [TestInitialize]
    public async Task Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-upload-http", Guid.NewGuid().ToString("N"));
        _port = FreePort();
        _cts = new CancellationTokenSource();

        var service = new InboundAttachmentService(
            new AttachmentStorage(_root), NullLogger<InboundAttachmentService>.Instance);

        _endpoint = new AttachmentUploadEndpoint(
            service,
            new AttachmentUploadEndpointOptions { Enabled = true, Port = _port },
            NullLogger<AttachmentUploadEndpoint>.Instance);

        await _endpoint.StartAsync(_cts.Token);

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        await WaitForReadyAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _cts.CancelAsync();
        try { await _endpoint.StopAsync(CancellationToken.None); } catch { /* shutting down */ }
        _cts.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Post_Screenshot_WritesItAndReturnsAPathReference()
    {
        // 3 MB — comfortably larger than anything anyone would want sitting in a broker queue,
        // which is the whole reason this endpoint exists.
        var bytes = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        var response = await PostAsync("screenshot.png", "image/png", bytes);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AttachmentUploadResponse>();

        Assert.IsNotNull(body);
        Assert.IsTrue(body!.Success, body.Error);
        Assert.AreEqual("screenshot.png", body.Attachment!.Path);
        Assert.AreEqual("image/png", body.Attachment.Mime);

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "screenshot.png")),
            "3 MB must land on the volume byte-for-byte, without ever touching the broker.");
    }

    [TestMethod]
    public async Task Post_DisallowedType_IsRejectedByTheSameRulesAsTheBusPath()
    {
        var response = await PostAsync("payload.exe", "application/x-msdownload", [1, 2, 3]);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AttachmentUploadResponse>();

        Assert.IsFalse(body!.Success);
        StringAssert.Contains(body.Error!, "not accepted");
        Assert.AreEqual(0, Directory.GetFiles(_root).Length,
            "An endpoint that writes first and validates second would be a file-write API.");
    }

    [TestMethod]
    public async Task Post_TypeDisagreeingWithExtension_IsRejected()
    {
        var response = await PostAsync("invoice.png", "application/pdf", [1, 2, 3]);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Post_OverTheSizeCap_IsRefusedWithoutBufferingTheWholeFile()
    {
        var oversized = new byte[InboundAttachmentService.MaxBytes + 1024];

        var response = await PostAsync("huge.png", "image/png", oversized);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Post_WithoutAFilePart_IsABadRequest()
    {
        using var content = new MultipartFormDataContent { { new StringContent("sess-1"), "sessionId" } };

        var response = await _client.PostAsync("/attachments", content);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Post_NotMultipart_IsABadRequest()
    {
        using var content = new StringContent("not a form");

        var response = await _client.PostAsync("/attachments", content);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Disabled_DoesNotListen()
    {
        // With the endpoint off, clients fall back to the bus. Proving it does not bind matters
        // because "disabled" that still listens is a surprise in a deployment that turned it off.
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var disabled = new AttachmentUploadEndpoint(
            new InboundAttachmentService(
                new AttachmentStorage(_root), NullLogger<InboundAttachmentService>.Instance),
            new AttachmentUploadEndpointOptions { Enabled = false, Port = port },
            NullLogger<AttachmentUploadEndpoint>.Instance);

        await disabled.StartAsync(cts.Token);

        // Refused or timed out — either is "nothing is listening". Which one you get depends on
        // the platform's behaviour for an unbound port, so asserting the exception type would
        // make this a test of Windows rather than of the endpoint.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var probe = await client.GetAsync($"http://127.0.0.1:{port}/health");
            Assert.Fail($"Expected nothing on port {port}, got {(int)probe.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Expected: the endpoint is disabled, so nothing bound the port.
        }

        await disabled.StopAsync(CancellationToken.None);
    }

    private async Task<HttpResponseMessage> PostAsync(string fileName, string mime, byte[] data)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(file, "file", fileName);
        content.Add(new StringContent("sess-http"), "sessionId");

        return await _client.PostAsync("/attachments", content);
    }

    private async Task WaitForReadyAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var probe = await _client.GetAsync("/health");
                if (probe.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Kestrel is still binding.
            }
            await Task.Delay(50);
        }

        Assert.Fail($"Upload endpoint did not become ready on port {_port}.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
