using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace RockBot.ResearchAgent.Tests;

/// <summary>
/// The research server's MCP surface, in-process over real streamable HTTP: the real
/// <see cref="ResearchTools"/> with the production transport and Tasks settings, and a fake
/// runner and scoper behind it.
/// </summary>
internal sealed class ResearchServerHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<McpClient> _clients = [];

    private ResearchServerHarness(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<ResearchServerHarness> StartAsync(
        IResearchRunner runner, IResearchScoper scoper, TimeSpan? scopeTokenLifetime = null)
    {
        var tools = new ResearchTools(
            runner, scoper,
            new ResearchScopeToken(new EphemeralDataProtectionProvider(), scopeTokenLifetime ?? TimeSpan.FromMinutes(10)),
            NullLogger<ResearchTools>.Instance);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 },
                t => t.ExecutionModeSelector = ResearchCallMode.Select)
            .WithTools([tools.CreateTool()]);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ResearchServerHarness(app, $"{address}/mcp");
    }

    /// <summary>Connects a client whose elicitation handler is <paramref name="elicit"/> (none when null).</summary>
    public async Task<McpClient> ConnectAsync(
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicit = null)
    {
        var options = new McpClientOptions();
        if (elicit is not null)
            options.Handlers = new McpClientHandlers { ElicitationHandler = elicit };
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(Url) }), options);
        _clients.Add(client);
        return client;
    }

    public static CallToolRequestParams Call(string question, string? context = null, string? constraints = null)
    {
        var args = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["question"] = System.Text.Json.JsonSerializer.SerializeToElement(question),
        };
        if (context is not null)
            args["context"] = System.Text.Json.JsonSerializer.SerializeToElement(context);
        if (constraints is not null)
            args["constraints"] = System.Text.Json.JsonSerializer.SerializeToElement(constraints);
        return new CallToolRequestParams { Name = ResearchTools.ToolName, Arguments = args };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Records each run and answers from a template; can hold runs open and track overlap.</summary>
internal sealed class FakeResearchRunner : IResearchRunner
{
    private int _running;
    private int _peak;

    public ConcurrentQueue<ResearchRequest> Requests { get; } = new();

    public IReadOnlyList<ResearchSource> Sources { get; init; } =
        [new("https://example.com/a", "Page A"), new("https://example.com/b")];

    /// <summary>When set, each run waits for it before answering.</summary>
    public TaskCompletionSource? Hold { get; init; }

    /// <summary>When set, each run takes this long.</summary>
    public TimeSpan Delay { get; init; }

    public int Peak => Volatile.Read(ref _peak);

    public async Task<ResearchOutcome> RunAsync(
        ResearchRequest request, string runId, Func<string, CancellationToken, Task>? onProgress, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
        try
        {
            if (Hold is not null)
                await Hold.Task.WaitAsync(cancellationToken);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            return new ResearchOutcome($"answer to {request.Question} [{request.Interpretation ?? "as asked"}]", Sources);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>Returns a fixed scope and counts how often it was asked.</summary>
internal sealed class FakeScoper(ResearchScope scope) : IResearchScoper
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public static FakeScoper Clear() => new(ResearchScope.Clear);

    public static FakeScoper Mercury() => new(new ResearchScope(
        [new("planet", "Mercury, the planet"), new("element", "Mercury, the chemical element"), new("band", "Mercury Rev, the band")],
        "planet"));

    public Task<ResearchScope> ScopeAsync(ResearchRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(scope);
    }
}
