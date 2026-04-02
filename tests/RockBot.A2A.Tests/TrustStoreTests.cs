namespace RockBot.A2A.Tests;

[TestClass]
public class TrustStoreTests
{
    private string _tempFile = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"trust-test-{Guid.NewGuid():N}.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [TestMethod]
    public async Task GetOrCreate_NewAgent_ReturnsObserveLevel()
    {
        var store = new FileAgentTrustStore(_tempFile);

        var entry = await store.GetOrCreateAsync("Agent1", CancellationToken.None);

        Assert.AreEqual("Agent1", entry.AgentId);
        Assert.AreEqual(AgentTrustLevel.Observe, entry.Level);
        Assert.AreEqual(0, entry.InteractionCount);
    }

    [TestMethod]
    public async Task GetOrCreate_ExistingAgent_ReturnsSameEntry()
    {
        var store = new FileAgentTrustStore(_tempFile);

        var first = await store.GetOrCreateAsync("Agent1", CancellationToken.None);
        var second = await store.GetOrCreateAsync("Agent1", CancellationToken.None);

        Assert.AreEqual(first.AgentId, second.AgentId);
        Assert.AreEqual(first.FirstSeen, second.FirstSeen);
    }

    [TestMethod]
    public async Task Update_PersistsChanges()
    {
        var store = new FileAgentTrustStore(_tempFile);

        var entry = await store.GetOrCreateAsync("Agent1", CancellationToken.None);
        var updated = entry with
        {
            Level = AgentTrustLevel.Act,
            InteractionCount = 5,
            ApprovedSkills = ["notify-user"]
        };
        await store.UpdateAsync(updated, CancellationToken.None);

        // Reload from file in a new store instance
        var store2 = new FileAgentTrustStore(_tempFile);
        var loaded = await store2.GetOrCreateAsync("Agent1", CancellationToken.None);

        Assert.AreEqual(AgentTrustLevel.Act, loaded.Level);
        Assert.AreEqual(5, loaded.InteractionCount);
        Assert.AreEqual(1, loaded.ApprovedSkills.Count);
        Assert.AreEqual("notify-user", loaded.ApprovedSkills[0]);
    }

    [TestMethod]
    public async Task List_ReturnsAllEntries()
    {
        var store = new FileAgentTrustStore(_tempFile);

        await store.GetOrCreateAsync("Agent1", CancellationToken.None);
        await store.GetOrCreateAsync("Agent2", CancellationToken.None);

        var entries = await store.ListAsync(CancellationToken.None);

        Assert.AreEqual(2, entries.Count);
    }

    [TestMethod]
    public async Task NullPath_WorksInMemoryOnly()
    {
        var store = new FileAgentTrustStore(null);

        var entry = await store.GetOrCreateAsync("Agent1", CancellationToken.None);
        Assert.AreEqual("Agent1", entry.AgentId);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.AreEqual(1, list.Count);
    }

    [TestMethod]
    public async Task CaseInsensitive_LookupByAgentId()
    {
        var store = new FileAgentTrustStore(_tempFile);

        await store.GetOrCreateAsync("TestAgent", CancellationToken.None);
        var entry = await store.GetOrCreateAsync("testagent", CancellationToken.None);

        Assert.AreEqual("TestAgent", entry.AgentId);
    }
}
