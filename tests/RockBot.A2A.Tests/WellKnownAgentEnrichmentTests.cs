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
    public async Task StartAsync_FetchesWellKnown_AtHostRoot_WhenSeedUrlHasPath()
    {
        // Seed URL with a path prefix (e.g. SocialAgent at "http://host/a2a/").
        // Well-known is host-relative per RFC 8615 — must hit "http://host/.well-known/agent-card.json",
        // not "http://host/a2a/.well-known/agent-card.json".
        var handler = new RecordingHandler("""{"agentName":"Bob","description":"x"}""");
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200/a2a/" }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        Assert.AreEqual("http://gateway-bob:5200/.well-known/agent-card.json",
            handler.LastRequestUri?.ToString(),
            "Well-known must be fetched from the URL authority, not the seed path.");
    }

    [TestMethod]
    public async Task StartAsync_ExtractsProtocolVersion_FromV1SupportedInterfaces()
    {
        // A2A v1 card: protocolVersion lives inside supportedInterfaces[], not at the top.
        const string v1Card = """
        {
          "name": "SocialAgent",
          "description": "Social media agent",
          "version": "1.3.0.0",
          "supportedInterfaces": [
            { "url": "/a2a", "protocolBinding": "JSONRPC",  "protocolVersion": "1.0" },
            { "url": "/a2a", "protocolBinding": "HTTPJSON", "protocolVersion": "1.0" }
          ],
          "capabilities": { "streaming": true },
          "skills": [{ "id": "engagement-summary", "name": "Engagement", "description": "x" }]
        }
        """;
        var handler = new RecordingHandler(v1Card);
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard { AgentName = "SocialAgent", Url = "http://social-agent/a2a/" }
            ]
        };

        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));

        await directory.StartAsync(CancellationToken.None);

        var card = directory.GetAgent("SocialAgent");
        Assert.IsNotNull(card);
        Assert.AreEqual("1.0", card.ProtocolVersion,
            "v1 card protocolVersion must be extracted from supportedInterfaces[0].");
        Assert.AreEqual(true, card.SupportsStreaming,
            "v1 card streaming capability must be extracted from capabilities.streaming.");
        Assert.AreEqual("Social media agent", card.Description);
        Assert.IsNotNull(card.Skills);
        Assert.AreEqual(1, card.Skills.Count);
    }

    [TestMethod]
    public void ExtractRemoteFields_ReturnsNulls_ForMalformedJson()
    {
        var fields = AgentDirectory.ExtractRemoteFields("not json");
        Assert.IsNull(fields.ProtocolVersion);
        Assert.IsNull(fields.SupportsStreaming);
    }

    [TestMethod]
    public void ExtractRemoteFields_ReturnsNulls_WhenFieldsAbsent()
    {
        var fields = AgentDirectory.ExtractRemoteFields("""{"agentName":"x"}""");
        Assert.IsNull(fields.ProtocolVersion);
        Assert.IsNull(fields.SupportsStreaming);
    }

    [TestMethod]
    public void ExtractRemoteFields_HandlesNullStreaming()
    {
        // SocialAgent emits capabilities.streaming: null — must round-trip as null,
        // not as false (which would be a meaningful "no streaming" signal).
        var fields = AgentDirectory.ExtractRemoteFields(
            """{"capabilities":{"streaming":null}}""");
        Assert.IsNull(fields.SupportsStreaming);
    }

    [TestMethod]
    public void ExtractRemoteFields_PrefersTopLevelProtocolVersion_OverInterfaces()
    {
        // If both layouts are present, the explicit top-level wins (v0.3-style).
        var fields = AgentDirectory.ExtractRemoteFields(
            """{"protocolVersion":"0.3","supportedInterfaces":[{"protocolVersion":"1.0"}]}""");
        Assert.AreEqual("0.3", fields.ProtocolVersion);
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

    [TestMethod]
    public async Task RefreshAllWellKnownAsync_RefetchesAndUpdatesSkills()
    {
        var initial = new AgentCard
        {
            AgentName = "Bob",
            Description = "Bob's agent",
            Version = "1.0",
            Skills = [new AgentSkill { Id = "old-skill", Name = "Old", Description = "x" }]
        };
        var handler = new RecordingHandler(JsonSerializer.Serialize(initial, CamelCase));
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents = [new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200" }]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);

        var updated = new AgentCard
        {
            AgentName = "Bob",
            Description = "Bob's agent v2",
            Version = "1.1",
            Skills =
            [
                new AgentSkill { Id = "old-skill", Name = "Old", Description = "x" },
                new AgentSkill { Id = "new-skill", Name = "New", Description = "y" }
            ]
        };
        handler.Body = JsonSerializer.Serialize(updated, CamelCase);

        var results = await directory.RefreshAllWellKnownAsync(CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Bob", results[0].AgentName);
        Assert.IsTrue(results[0].Refreshed);
        Assert.IsTrue(results[0].SkillsChanged);

        var card = directory.GetAgent("Bob");
        Assert.IsNotNull(card?.Skills);
        Assert.AreEqual(2, card.Skills.Count);
        Assert.IsTrue(card.Skills.Any(s => s.Id == "new-skill"));
    }

    [TestMethod]
    public async Task RefreshAllWellKnownAsync_PreservesOfflineOverride()
    {
        var handler = new RecordingHandler("""{"agentName":"Bob","skills":[{"id":"remote"}]}""");
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard
                {
                    AgentName = "Bob",
                    Url = "http://gateway-bob:5200",
                    Skills = [new AgentSkill { Id = "override", Name = "O", Description = "x" }]
                }
            ]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);

        var startCount = handler.RequestCount;
        var results = await directory.RefreshAllWellKnownAsync(CancellationToken.None);

        Assert.AreEqual(0, results.Count, "Offline overrides should not be refreshed.");
        Assert.AreEqual(startCount, handler.RequestCount, "No HTTP request should have been made.");

        var card = directory.GetAgent("Bob");
        Assert.AreEqual("override", card?.Skills?.Single().Id);
    }

    [TestMethod]
    public async Task RefreshAllWellKnownAsync_ClearsLlmSummary_WhenSkillsChange()
    {
        var initial = new AgentCard
        {
            AgentName = "Bob",
            Skills = [new AgentSkill { Id = "old", Name = "Old", Description = "x" }]
        };
        var handler = new RecordingHandler(JsonSerializer.Serialize(initial, CamelCase));
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents = [new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200" }]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);
        directory.SetSummary("Bob", "An agent that does old things.");
        Assert.AreEqual("An agent that does old things.", directory.GetAllEntries().Single().LlmSummary);

        var updated = new AgentCard
        {
            AgentName = "Bob",
            Skills = [new AgentSkill { Id = "new", Name = "New", Description = "y" }]
        };
        handler.Body = JsonSerializer.Serialize(updated, CamelCase);

        var results = await directory.RefreshAllWellKnownAsync(CancellationToken.None);

        Assert.IsTrue(results.Single().SkillsChanged);
        Assert.IsNull(directory.GetAllEntries().Single().LlmSummary,
            "LlmSummary must be cleared when skills change so a fresh summary is regenerated.");
    }

    [TestMethod]
    public async Task RefreshAllWellKnownAsync_PreservesLlmSummary_WhenSkillsUnchanged()
    {
        var card = new AgentCard
        {
            AgentName = "Bob",
            Description = "v1",
            Skills = [new AgentSkill { Id = "same-skill", Name = "Same", Description = "x" }]
        };
        var handler = new RecordingHandler(JsonSerializer.Serialize(card, CamelCase));
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents = [new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200" }]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);
        directory.SetSummary("Bob", "Bob does things.");

        // Same skill set, just a bumped description — skills unchanged should preserve summary.
        var same = card with { Description = "v2" };
        handler.Body = JsonSerializer.Serialize(same, CamelCase);

        var results = await directory.RefreshAllWellKnownAsync(CancellationToken.None);

        Assert.IsTrue(results.Single().Refreshed);
        Assert.IsFalse(results.Single().SkillsChanged);
        Assert.AreEqual("Bob does things.", directory.GetAllEntries().Single().LlmSummary);
    }

    [TestMethod]
    public async Task RefreshAgentCardAsync_ReturnsNotFound_ForUnknownAgent()
    {
        var handler = new RecordingHandler("");
        var options = new A2AOptions { DirectoryPersistencePath = string.Empty };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);

        var result = await directory.RefreshAgentCardAsync("nobody", CancellationToken.None);

        Assert.IsFalse(result.Refreshed);
        Assert.IsFalse(result.SkillsChanged);
        Assert.AreEqual("agent not found", result.Reason);
    }

    [TestMethod]
    public async Task RefreshAgentCardAsync_ReturnsSkipped_ForOfflineOverride()
    {
        var handler = new RecordingHandler("""{"agentName":"Bob","skills":[{"id":"remote"}]}""");
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents =
            [
                new AgentCard
                {
                    AgentName = "Bob",
                    Url = "http://gateway-bob:5200",
                    Skills = [new AgentSkill { Id = "override", Name = "O", Description = "x" }]
                }
            ]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);

        var beforeCount = handler.RequestCount;
        var result = await directory.RefreshAgentCardAsync("Bob", CancellationToken.None);

        Assert.IsFalse(result.Refreshed);
        Assert.AreEqual("offline override", result.Reason);
        Assert.AreEqual(beforeCount, handler.RequestCount);
    }

    [TestMethod]
    public async Task RefreshAgentCardAsync_RefetchesByName()
    {
        var initial = new AgentCard
        {
            AgentName = "Bob",
            Skills = [new AgentSkill { Id = "v1", Name = "V1", Description = "x" }]
        };
        var handler = new RecordingHandler(JsonSerializer.Serialize(initial, CamelCase));
        var options = new A2AOptions
        {
            DirectoryPersistencePath = string.Empty,
            WellKnownAgents = [new AgentCard { AgentName = "Bob", Url = "http://gateway-bob:5200" }]
        };
        var directory = new AgentDirectory(options, NullLogger<AgentDirectory>.Instance, new StubFactory(handler));
        await directory.StartAsync(CancellationToken.None);

        var updated = initial with
        {
            Skills = [new AgentSkill { Id = "v2", Name = "V2", Description = "y" }]
        };
        handler.Body = JsonSerializer.Serialize(updated, CamelCase);

        var beforeCount = handler.RequestCount;
        var result = await directory.RefreshAgentCardAsync("Bob", CancellationToken.None);

        Assert.IsTrue(result.Refreshed);
        Assert.IsTrue(result.SkillsChanged);
        Assert.AreEqual(beforeCount + 1, handler.RequestCount);
        Assert.AreEqual("v2", directory.GetAgent("Bob")?.Skills?.Single().Id);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status;

        public RecordingHandler(string body = "", HttpStatusCode status = HttpStatusCode.OK)
        {
            Body = body;
            _status = status;
        }

        public string Body { get; set; }
        public Uri? LastRequestUri { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }
        public int RequestCount { get; private set; }

        public void SetStatus(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = request.Headers;
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
