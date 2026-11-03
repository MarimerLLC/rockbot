using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// The council's MCP surface, in-process over real streamable HTTP: the real
/// <see cref="CouncilTools"/> with the production transport and Tasks settings, and a fake
/// <see cref="ICouncil"/> behind it.
/// </summary>
internal sealed class CouncilServerHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<McpClient> _clients = [];

    private CouncilServerHarness(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    /// <summary>The real tool over <paramref name="council"/>, for hosts that build their own server.</summary>
    public static CouncilTools CreateTools(ICouncil council, TimeSpan? tokenLifetime = null, int overallTimeoutSeconds = 60) => new(
        council,
        new CouncilClarificationToken(new EphemeralDataProtectionProvider(), tokenLifetime ?? TimeSpan.FromMinutes(10)),
        Options.Create(new CouncilOptions { OverallTimeoutSeconds = overallTimeoutSeconds }),
        NullLogger<CouncilTools>.Instance);

    public static async Task<CouncilServerHarness> StartAsync(ICouncil council, TimeSpan? tokenLifetime = null)
    {
        var tools = CreateTools(council, tokenLifetime);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 },
                t => t.ExecutionModeSelector = CouncilCallMode.Select)
            .WithTools([tools.CreateTool()]);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new CouncilServerHarness(app, $"{address}/mcp");
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

    public static CallToolRequestParams Call(string question, IReadOnlyList<string>? options = null, string? context = null)
    {
        var args = new Dictionary<string, JsonElement> { ["question"] = JsonSerializer.SerializeToElement(question) };
        if (options is not null)
            args["options"] = JsonSerializer.SerializeToElement(options);
        if (context is not null)
            args["context"] = JsonSerializer.SerializeToElement(context);
        return new CallToolRequestParams { Name = CouncilTools.ToolName, Arguments = args };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Returns a fixed selection, records each deliberation and answers from a template; can hold
/// deliberations open and track overlap.
/// </summary>
internal sealed class FakeCouncil(SelectorOutput selection) : ICouncil
{
    private int _selects;
    private int _running;
    private int _peak;

    public static SelectorOutput Selection(IReadOnlyList<string>? missing = null, bool tooThin = false, IReadOnlyList<string>? needs = null) =>
        new([new SelectedPersona("skeptic"), new SelectedPersona("engineer")], false, false, "fake",
            missing ?? [], tooThin, needs ?? []);

    public static FakeCouncil Clear() => new(Selection());

    /// <summary>Select finds the time horizon and risk appetite open (and the preferred option, when there are options).</summary>
    public static FakeCouncil Open() => new(Selection(
        [CouncilChoices.PreferredOptionField, CouncilChoices.TimeHorizonField, CouncilChoices.RiskAppetiteField]));

    public static FakeCouncil TooThin() => new(Selection(tooThin: true, needs: ["what 'it' is", "the goal"]));

    public ConcurrentQueue<CouncilRequest> Deliberations { get; } = new();

    public int Selects => Volatile.Read(ref _selects);

    public int Peak => Volatile.Read(ref _peak);

    /// <summary>When set, each deliberation waits for it before answering.</summary>
    public TaskCompletionSource? Hold { get; init; }

    /// <summary>When set, each deliberation takes this long.</summary>
    public TimeSpan Delay { get; init; }

    public Task<SelectorOutput> SelectAsync(CouncilRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _selects);
        // Like SelectStep.ApplyRequest: only fields the caller didn't supply are missing.
        return Task.FromResult(selection with
        {
            Missing = [.. (selection.Missing ?? []).Where(m => !request.Has(m)
                && (m != CouncilChoices.PreferredOptionField || request.Options.Count >= 2))],
        });
    }

    public async Task<CouncilResponse> DeliberateAsync(CouncilRequest request, SelectorOutput chosen, string runId, CancellationToken ct)
    {
        Deliberations.Enqueue(request);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
        try
        {
            if (Hold is not null)
                await Hold.Task.WaitAsync(ct);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);

            return new CouncilResponse(
                Question: request.Question,
                Personas: [.. chosen.Personas.Select(p => new PersonaView(p.Id, $"{p.Id} view", [], []))],
                Tensions: [],
                Synthesis: $"Advice on {request.Question} (horizon={request.TimeHorizon ?? "?"}, risk={request.RiskAppetite ?? "?"}, leaning={request.PreferredOption ?? "?"})",
                Confidence: "medium",
                Metadata: new CouncilMetadata(false, false, chosen.Personas.Count, 1, 4, "hash", chosen.Rationale,
                    chosen.Personas.Count, [], request.Clarification),
                Assumptions: [.. request.Unresolved.Select(u => $"Assumed a typical {u.Replace('_', ' ')}.")]);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
