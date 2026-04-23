using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.A2A.Tests;

[TestClass]
public class WellKnownAgentEnrichmentTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public async Task StartAsync_EnrichesSkeletonEntry_FromRemoteAgentCard()
    {
        var remote = new AgentCard
        {
            AgentName = "Bob",
            Description = "Bob's agent",
            Version = "1.0",
            ProtocolVersion = "1.0",
            SupportsStreaming = true,
            Skills =
            [
                new AgentSkill { Id = "notify-user", Name = "Notify User", Description = "x" },
                new AgentSkill { Id = "query-availability", Name = "Query", Description = "y" }
            ]
        };

        var handler = new RecordingHandler(JsonSerializer.Serialize(remote, CamelCase));
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard
                {
                    AgentName = "Bob",
                    Url = "http://gateway-bob:5200",
                    AuthHeaderName = "X-Api-Key",
                    AuthHeaderValueBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
                }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        var card = directory.GetAgent("Bob");
        Assert.IsNotNull(card);
        Assert.AreEqual("http://gateway-bob:5200", card.Url);
        Assert.AreEqual("X-Api-Key", card.AuthHeaderName);
        Assert.IsNotNull(card.Skills);
        Assert.AreEqual(2, card.Skills.Count);
        Assert.IsTrue(card.Skills.Any(s => s.Id == "notify-user"));
        Assert.AreEqual("Bob's agent", card.Description);
        Assert.AreEqual("1.0", card.ProtocolVersion);
        Assert.AreEqual(true, card.SupportsStreaming);

        Assert.AreEqual("http://gateway-bob:5200/.well-known/agent-card.json",
            handler.LastRequestUri?.ToString());
        Assert.IsTrue(handler.LastRequestHeaders?.Contains("X-Api-Key") == true);
        Assert.AreEqual("secret", handler.LastRequestHeaders?.GetValues("X-Api-Key").First());

        var entry = directory.GetAllEntries().Single();
        Assert.IsTrue(entry.IsWellKnown);
    }

    [TestMethod]
    public async Task StartAsync_PreservesPrepopulatedSkills_AsOffllineOverride()
    {
        var handler = new RecordingHandler("""{"agentName":"Bob","skills":[{"id":"other"}]}""");
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard
                {
                    AgentName = "Bob",
                    Url = "http://gateway-bob:5200",
                    Skills = [new AgentSkill { Id = "override-skill", Name = "Override", Description = "z" }]
                }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        var card = directory.GetAgent("Bob");
        Assert.IsNotNull(card);
        Assert.IsNotNull(card.Skills);
        Assert.AreEqual(1, card.Skills.Count);
        Assert.AreEqual("override-skill", card.Skills[0].Id);
        Assert.IsNull(handler.LastRequestUri, "No fetch should have happened when skills were pre-populated");
    }

    [TestMethod]
    public async Task StartAsync_LeavesSkeletonIntact_WhenFetchFails()
    {
        var handler = new RecordingHandler(status: HttpStatusCode.ServiceUnavailable);
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard
                {
                    AgentName = "Bob",
                    Url = "http://gateway-bob:5200",
                    AuthHeaderName = "X-Api-Key",
                    AuthHeaderValueBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("secret"))
                }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        var card = directory.GetAgent("Bob");
        Assert.IsNotNull(card, "Agent should remain callable by name even if enrichment fails");
        Assert.IsNull(card.Skills);
        Assert.AreEqual("http://gateway-bob:5200", card.Url);
        Assert.AreEqual("X-Api-Key", card.AuthHeaderName);

        var entry = directory.GetAllEntries().Single();
        Assert.IsTrue(entry.IsWellKnown);
    }

    [TestMethod]
    public async Task StartAsync_SkipsEnrichment_WhenUrlMissing()
    {
        var handler = new RecordingHandler("""{}""");
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard { AgentName = "Local-Only" }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        Assert.IsNotNull(directory.GetAgent("Local-Only"));
        Assert.IsNull(handler.LastRequestUri, "No URL means no fetch");
    }

    [TestMethod]
    public async Task StartAsync_NoEnrichment_WhenHttpFactoryNotProvided()
    {
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200" }
            ]
        };

        // Back-compat: the old two-arg ctor is still valid — enrichment is simply skipped.
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance);

        await directory.StartAsync(CancellationToken.None);

        var card = directory.GetAgent("Bob");
        Assert.IsNotNull(card);
        Assert.IsNull(card.Skills);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string body = "", HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public Uri? LastRequestUri { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = request.Headers;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
