extern alias research;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using research::RockBot.ResearchAgent;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Host;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// The council's research client (Phase 3 of <c>design/research-agent-mcp.md</c>) against the
/// research server's real <c>research</c> tool over streamable HTTP with Tasks. The research
/// runner and scoper are fakes; the tool, the Tasks extension, the poll loop and the
/// <c>llm</c> elicitation responder are real.
/// </summary>
[TestClass]
public class ResearchMcpClientTests
{
    private sealed class Runner : IResearchRunner
    {
        public List<ResearchRequest> Requests { get; } = [];
        public TaskCompletionSource? Hold { get; init; }
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ResearchOutcome> RunAsync(
            ResearchRequest request, string runId, Func<string, CancellationToken, Task>? onProgress, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Hold is not null)
            {
                try { await Hold.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new ResearchOutcome($"findings on {request.Question} [{request.Interpretation ?? "as asked"}]",
                [new ResearchSource("https://example.com/a", "Page A")]);
        }
    }

    private sealed class Scoper(bool ambiguous) : IResearchScoper
    {
        public Task<ResearchScope> ScopeAsync(ResearchRequest request, CancellationToken ct) =>
            Task.FromResult(ambiguous
                ? new ResearchScope(
                    [new("planet", "Mercury, the planet"), new("element", "Mercury, the chemical element")], "planet")
                : ResearchScope.Clear);
    }

    /// <summary>The llm responder's model: answers with <paramref name="reply"/> and records the prompt.</summary>
    private sealed class ResponderLlm(string reply) : ILlmClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            GetResponseAsync(messages, ModelTier.Low, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct)
        {
            lock (Prompts) Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private Server(WebApplication app, string url) { _app = app; Url = url; }
        public string Url { get; }

        public static async Task<Server> StartAsync(IResearchRunner runner, IResearchScoper scoper)
        {
            var tool = new ResearchTools(runner, scoper,
                new ResearchScopeToken(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(10)),
                NullLogger<ResearchTools>.Instance).CreateTool();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 },
                    t => t.ExecutionModeSelector = ResearchCallMode.Select)
                .WithTools([tool]);
            var app = builder.Build();
            app.MapMcp("/mcp");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Server(app, $"{address}/mcp");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static ResearchMcpClient Client(string url, ILlmClient? llm = null, int timeoutSeconds = 30) =>
        new(Options.Create(new CouncilOptions { ResearchServerUrl = url, ResearchAgentTimeoutSeconds = timeoutSeconds }),
            llm ?? new ResponderLlm("""{"action":"decline","reason":"not in the call"}"""),
            NullLoggerFactory.Instance);

    [TestMethod]
    public async Task AClearQuestion_ReturnsFindingsAndSources_AndPassesTheContext()
    {
        var runner = new Runner();
        await using var server = await Server.StartAsync(runner, new Scoper(ambiguous: false));
        await using var client = Client(server.Url);

        var text = await client.InvokeAsync("How far is Mars?", "council question: should we fly there?", CancellationToken.None);

        StringAssert.StartsWith(text, "findings on How far is Mars? [as asked]");
        StringAssert.Contains(text, "Sources consulted:");
        StringAssert.Contains(text, "https://example.com/a");
        Assert.AreEqual("council question: should we fly there?", runner.Requests.Single().Context);
    }

    [TestMethod]
    public async Task ResearchsQuestion_IsAnsweredByTheLlmResponder_FromTheCallsArguments()
    {
        var runner = new Runner();
        var llm = new ResponderLlm("""{"action":"accept","content":{"interpretation":"element"}}""");
        await using var server = await Server.StartAsync(runner, new Scoper(ambiguous: true));
        await using var client = Client(server.Url, llm);

        var text = await client.InvokeAsync("Mercury", "We are weighing mercury exposure risks in a chemistry lab.", CancellationToken.None);

        StringAssert.StartsWith(text, "Researched as: Mercury, the chemical element");
        Assert.AreEqual("Mercury, the chemical element", runner.Requests.Single().Interpretation);
        var prompt = llm.Prompts.Single();
        StringAssert.Contains(prompt, "chemistry lab", "the responder sees the call's own arguments");
        StringAssert.Contains(prompt, "element (\\\"Mercury, the chemical element\\\")".Replace("\\\"", "\""));
    }

    [TestMethod]
    public async Task ADeclinedQuestion_StillReturnsResearch_OnTheMostLikelyReading()
    {
        var runner = new Runner();
        await using var server = await Server.StartAsync(runner, new Scoper(ambiguous: true));
        await using var client = Client(server.Url);

        var text = await client.InvokeAsync("Mercury", null, CancellationToken.None);

        StringAssert.StartsWith(text, "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task ATimeout_CancelsTheTaskOnTheServer()
    {
        var runner = new Runner { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = await Server.StartAsync(runner, new Scoper(ambiguous: false));
        await using var client = Client(server.Url, timeoutSeconds: 5);

        var text = await client.InvokeAsync("A long one", null, CancellationToken.None);

        Assert.AreEqual("(research timed out)", text);
        await runner.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task AnUnreachableServer_IsAFailureSentinel_NotAnException()
    {
        await using var client = Client("http://127.0.0.1:1/mcp", timeoutSeconds: 10);

        var text = await client.InvokeAsync("Anything", null, CancellationToken.None);

        StringAssert.StartsWith(text, "(research");
    }

    [TestMethod]
    public async Task AnEmptyQuestion_IsNotSent() =>
        Assert.AreEqual("(empty research question)",
            await Client("http://127.0.0.1:1/mcp").InvokeAsync("  ", null, CancellationToken.None));

    [TestMethod]
    public void AnErrorResult_BecomesAFailureSentinel()
    {
        var text = ResearchMcpClient.Format(new ModelContextProtocol.Protocol.CallToolResult
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "research needs a question." }],
        });

        Assert.AreEqual("(research failed: research needs a question.)", text);
    }
}
