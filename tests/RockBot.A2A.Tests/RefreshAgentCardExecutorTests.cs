using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools;

namespace RockBot.A2A.Tests;

[TestClass]
public class RefreshAgentCardExecutorTests
{
    private static AgentCardSummarizer CreateSummarizer() =>
        new(new ServiceCollection().BuildServiceProvider(), NullLogger<AgentCardSummarizer>.Instance);

    private static ToolInvokeRequest Request(string? args) =>
        new()
        {
            ToolCallId = "call-1",
            ToolName = "refresh_agent_card",
            Arguments = args
        };

    [TestMethod]
    public async Task ReturnsError_WhenAgentNameMissing()
    {
        var directory = new FakeDirectory();
        var executor = new RefreshAgentCardExecutor(
            directory, CreateSummarizer(), NullLogger<RefreshAgentCardExecutor>.Instance);

        var response = await executor.ExecuteAsync(Request("""{}"""), CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "agent_name");
        Assert.AreEqual(0, directory.RefreshCallCount,
            "Directory should not be touched when agent_name is missing.");
    }

    [TestMethod]
    public async Task ReturnsError_OnInvalidJson()
    {
        var directory = new FakeDirectory();
        var executor = new RefreshAgentCardExecutor(
            directory, CreateSummarizer(), NullLogger<RefreshAgentCardExecutor>.Instance);

        var response = await executor.ExecuteAsync(Request("not json"), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task ReturnsSuccessJson_WhenAgentRefreshed()
    {
        var directory = new FakeDirectory
        {
            NextResult = new AgentCardRefreshResult("Bob", Refreshed: true, SkillsChanged: true, Reason: null)
        };
        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "Bob",
            Skills = [new AgentSkill { Id = "s1", Name = "S1", Description = "x" }]
        });
        var executor = new RefreshAgentCardExecutor(
            directory, CreateSummarizer(), NullLogger<RefreshAgentCardExecutor>.Instance);

        var response = await executor.ExecuteAsync(
            Request("""{"agent_name":"Bob"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        using var doc = JsonDocument.Parse(response.Content!);
        Assert.AreEqual("Bob", doc.RootElement.GetProperty("agentName").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("refreshed").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("skillsChanged").GetBoolean());
        StringAssert.Contains(doc.RootElement.GetProperty("status").GetString()!, "refreshed");
    }

    [TestMethod]
    public async Task ReturnsNotFoundStatus_WhenDirectoryReportsNotFound()
    {
        var directory = new FakeDirectory
        {
            NextResult = new AgentCardRefreshResult("Ghost", false, false, "agent not found")
        };
        var executor = new RefreshAgentCardExecutor(
            directory, CreateSummarizer(), NullLogger<RefreshAgentCardExecutor>.Instance);

        var response = await executor.ExecuteAsync(
            Request("""{"agent_name":"Ghost"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        using var doc = JsonDocument.Parse(response.Content!);
        Assert.AreEqual("not found", doc.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("refreshed").GetBoolean());
    }

    [TestMethod]
    public async Task ReturnsSkippedStatus_WhenOfflineOverride()
    {
        var directory = new FakeDirectory
        {
            NextResult = new AgentCardRefreshResult("Bob", false, false, "offline override")
        };
        var executor = new RefreshAgentCardExecutor(
            directory, CreateSummarizer(), NullLogger<RefreshAgentCardExecutor>.Instance);

        var response = await executor.ExecuteAsync(
            Request("""{"agent_name":"Bob"}"""), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        using var doc = JsonDocument.Parse(response.Content!);
        StringAssert.Contains(doc.RootElement.GetProperty("status").GetString()!, "offline override");
    }

    private sealed class FakeDirectory : IAgentDirectory
    {
        private readonly Dictionary<string, AgentDirectoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public AgentCardRefreshResult NextResult { get; set; } =
            new("", false, false, "no result configured");
        public int RefreshCallCount { get; private set; }

        public AgentCard? GetAgent(string agentName) =>
            _entries.TryGetValue(agentName, out var e) ? e.Card : null;

        public IReadOnlyList<AgentCard> GetAllAgents() =>
            _entries.Values.Select(e => e.Card).ToList();

        public IReadOnlyList<AgentCard> FindBySkill(string skillId) => Array.Empty<AgentCard>();

        public IReadOnlyList<AgentDirectoryEntry> GetAllEntries() => _entries.Values.ToList();

        public void AddOrUpdate(AgentCard card) =>
            _entries[card.AgentName] = new AgentDirectoryEntry
            {
                Card = card,
                LastSeenAt = DateTimeOffset.UtcNow
            };

        public void Remove(string agentName) => _entries.Remove(agentName);

        public void SetSummary(string agentName, string summary)
        {
            if (_entries.TryGetValue(agentName, out var e))
                _entries[agentName] = e with { LlmSummary = summary };
        }

        public Task<IReadOnlyList<AgentCardRefreshResult>> RefreshAllWellKnownAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentCardRefreshResult>>(Array.Empty<AgentCardRefreshResult>());

        public Task<AgentCardRefreshResult> RefreshAgentCardAsync(string agentName, CancellationToken ct)
        {
            RefreshCallCount++;
            return Task.FromResult(NextResult);
        }
    }
}
