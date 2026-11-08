using System.Net;
using System.Text;
using System.Text.Json;
using McpServer.Gmail.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.Gmail.Tests;

/// <summary>Hosts the real Gmail server in-process against a fake Google with two accounts.</summary>
public abstract class GmailServerTestBase
{
    protected const string PersonalEmail = "simon@gmail.com";
    protected const string WorkEmail = "simon@client.example";

    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected FakeGoogle Google { get; } = new();

    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gmail:ClientId", "client-id");
            b.UseSetting("Gmail:ClientSecret", "client-secret");
            b.UseSetting("Gmail:Accounts:0:Name", "personal");
            b.UseSetting("Gmail:Accounts:0:Email", PersonalEmail);
            b.UseSetting("Gmail:Accounts:0:RefreshToken", "refresh-personal");
            b.UseSetting("Gmail:Accounts:1:Name", "work");
            b.UseSetting("Gmail:Accounts:1:Email", WorkEmail);
            b.UseSetting("Gmail:Accounts:1:RefreshToken", "refresh-work");
            b.UseSetting("Gmail:TimeZone", "Australia/Sydney");
            b.UseSetting("Gmail:MaxBodyChars", "1000");
            b.ConfigureTestServices(services =>
            {
                services.AddHttpClient<GoogleTokens>().ConfigurePrimaryHttpMessageHandler(() => Google);
                services.AddHttpClient<GmailApi>().ConfigurePrimaryHttpMessageHandler(() => Google);
            });
        });

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_factory.Server.BaseAddress, "/") },
            _factory.CreateClient(),
            ownsHttpClient: true);
        _client = await McpClient.CreateAsync(transport);
        return _client;
    }

    [TestCleanup]
    public async Task CleanupServerAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    protected async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        await (await ClientAsync()).CallToolAsync(tool, arguments);

    protected async Task<JsonElement> CallOkAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsFalse(result.IsError ?? false, $"{tool} failed: {TextOf(result)}");
        return JsonDocument.Parse(TextOf(result)).RootElement.Clone();
    }

    protected async Task<string> CallErrorAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsTrue(result.IsError ?? false, $"{tool} should have failed: {TextOf(result)}");
        return TextOf(result);
    }

    protected static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    protected static string B64Url(string text) => B64Url(Encoding.UTF8.GetBytes(text));

    protected static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Plays both Google's token endpoint (issuing "access-&lt;refresh token&gt;-&lt;n&gt;") and the Gmail API, which
/// answers from <see cref="Respond"/>. Records every request with its body.
/// </summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, string Path, string Query, string? Bearer, string? Body);

    private int _issued;

    public List<Request> Requests { get; } = [];

    public IEnumerable<Request> Gmail => Requests.Where(r => r.Path.StartsWith("/gmail/"));

    public IEnumerable<Request> TokenCalls => Requests.Where(r => r.Path == "/token");

    /// <summary>Overrides the token endpoint's answer, e.g. to return invalid_grant.</summary>
    public Func<Request, HttpResponseMessage>? TokenResponse { get; set; }

    public Func<Request, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Form(Request r, string key) =>
        r.Body!.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == key)[1] is var v ? Uri.UnescapeDataString(v) : "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var r = new Request(request.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query),
            request.Headers.Authorization?.Parameter, body);
        Requests.Add(r);

        if (request.RequestUri.Host == "oauth2.googleapis.com")
            return TokenResponse?.Invoke(r)
                   ?? Json($$"""{"access_token":"access-{{Form(r, "refresh_token")}}-{{++_issued}}","expires_in":3599,"token_type":"Bearer"}""");
        return Respond(r);
    }
}
