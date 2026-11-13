using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>
/// The <c>/attachments</c> endpoint serves files the agent wrote. These check, over the real
/// pipeline, that a type which can carry script is confined to an opaque origin (#669): an HTML
/// deck opened in its own tab must not run with this app's cookies.
/// </summary>
[TestClass]
public class AttachmentEndpointHeaderTests
{
    private const string SharedPathVariable = "ROCKBOT_SHARED_PATH";

    private static string? _previousSharedPath;
    private static string _sharedRoot = null!;
    private static WebApplicationFactory<Program> _factory = null!;
    private static HttpClient _client = null!;

    /// <summary>The app without its own hosted services, which would dial RabbitMQ on start.</summary>
    private sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                var appHostedServices = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .Where(d => d.ImplementationType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) != true)
                    .ToList();
                foreach (var descriptor in appHostedServices)
                    services.Remove(descriptor);
            });
    }

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _sharedRoot = Path.Combine(Path.GetTempPath(), "rb-attachment-headers", Guid.NewGuid().ToString("N"));
        var attachments = Path.Combine(_sharedRoot, "attachments");
        Directory.CreateDirectory(Path.Combine(attachments, "previews", "abc12345"));
        File.WriteAllText(Path.Combine(attachments, "previews", "abc12345", "deck.html"),
            "<!doctype html><html><body><script>document.title='ran'</script></body></html>");
        File.WriteAllText(Path.Combine(attachments, "diagram.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        File.WriteAllBytes(Path.Combine(attachments, "chart.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(attachments, "report.pdf"), "%PDF-1.7");

        // The app reads the shared path once, at startup.
        _previousSharedPath = Environment.GetEnvironmentVariable(SharedPathVariable);
        Environment.SetEnvironmentVariable(SharedPathVariable, _sharedRoot);
        _factory = new Host();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SharedPathVariable, _previousSharedPath);
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [TestMethod]
    public async Task Html_IsServedAsHtml_InAScriptOnlySandbox()
    {
        var response = await _client.GetAsync("/attachments?file=previews%2Fabc12345%2Fdeck.html");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        // With sniffing off, an undeclared charset means windows-1252 and mojibake for "·" and "é".
        Assert.AreEqual("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.AreEqual("sandbox allow-scripts", Header(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [TestMethod]
    public async Task Svg_IsFullySandboxed()
    {
        var response = await _client.GetAsync("/attachments?file=diagram.svg");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("sandbox", Header(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [TestMethod]
    [DataRow("chart.png", "image/png")]
    [DataRow("report.pdf", "application/pdf")]
    public async Task TypesThatCannotCarryScript_AreNotSandboxed_ButAreNotSniffed(string file, string mime)
    {
        var response = await _client.GetAsync($"/attachments?file={file}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(mime, response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(Header(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [TestMethod]
    public async Task Traversal_IsStillNotFound()
    {
        var response = await _client.GetAsync("/attachments?file=..%2F..%2Fsecret.html");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
