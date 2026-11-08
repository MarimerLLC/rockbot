using System.Net;
using System.Text;
using System.Text.Json;
using McpServer.Slack.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.Slack.Tests;

/// <summary>Hosts the real Slack server in-process against a fake Slack Web API with two workspaces.</summary>
public abstract class SlackServerTestBase
{
    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected FakeSlack Slack { get; } = new();

    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Slack:Workspaces:0:Name", "work");
            b.UseSetting("Slack:Workspaces:0:Token", "xoxp-work");
            b.UseSetting("Slack:Workspaces:0:TeamId", "T1");
            b.UseSetting("Slack:Workspaces:1:Name", "home");
            b.UseSetting("Slack:Workspaces:1:Token", "xoxp-home");
            b.UseSetting("Slack:TimeZone", "Australia/Sydney");
            b.UseSetting("Slack:MaxMessageChars", "500");
            b.ConfigureTestServices(services =>
                services.AddHttpClient<SlackApi>().ConfigurePrimaryHttpMessageHandler(() => Slack));
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
}

/// <summary>
/// Answers Slack Web API calls from <see cref="Responses"/>, keyed by method name, and records each
/// call's method, token and form fields. auth.test and users.info have sensible defaults.
/// </summary>
public sealed class FakeSlack : HttpMessageHandler
{
    public sealed record Call(string Method, string? Token, Dictionary<string, string> Form);

    public List<Call> Calls { get; } = [];

    public Dictionary<string, Func<Call, HttpResponseMessage>> Responses { get; } = new()
    {
        ["auth.test"] = c => Json($$"""{"ok":true,"team":"Team {{c.Token}}","team_id":"{{(c.Token == "xoxp-work" ? "T1" : "T2")}}","user":"simon","url":"https://x.slack.com/"}"""),
        ["users.info"] = c => Json(JsonSerializer.Serialize(new
        {
            ok = true,
            user = new { id = c.Form["user"], name = "u" + c.Form["user"], profile = new { display_name = "Name" + c.Form["user"] } }
        }))
    };

    public IEnumerable<Call> Of(string method) => Calls.Where(c => c.Method == method);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        var call = new Call(request.RequestUri!.AbsolutePath.Replace("/api/", ""), request.Headers.Authorization?.Parameter, form);
        Calls.Add(call);
        return Responses.TryGetValue(call.Method, out var respond)
            ? respond(call)
            : Json($$"""{"ok":false,"error":"unexpected_method_{{call.Method}}"}""");
    }
}
