using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Memory.Tests;

[TestClass]
public class MemoryToolsTests
{
    // -------------------------------------------------------------------------
    // SearchMemory — age formatting
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchMemory_EntryCreatedToday_ShowsToday()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "today");
    }

    [TestMethod]
    public async Task SearchMemory_EntryCreatedOneDayAgo_ShowsOneDayAgo()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-1)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "1 day ago");
    }

    [TestMethod]
    public async Task SearchMemory_EntryCreatedMultipleDaysAgo_ShowsNDaysAgo()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-7)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "7 days ago");
    }

    [TestMethod]
    public async Task SearchMemory_OneDayAgeLabel_IsNotPluralised()
    {
        // "1 days ago" would be wrong; must be "1 day ago"
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-1)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        Assert.IsFalse(result.Contains("1 days ago"), "Age label should be '1 day ago', not '1 days ago'");
    }

    [TestMethod]
    public async Task SearchMemory_MultipleEntries_EachShowsCorrectAge()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Content A", DateTimeOffset.UtcNow));
        memory.Add(Entry("id2", "Content B", DateTimeOffset.UtcNow.AddDays(-3)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "today");
        StringAssert.Contains(result, "3 days ago");
    }

    [TestMethod]
    public async Task SearchMemory_NoResults_ReturnsNoMemoriesFound()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.SearchMemory("anything");

        StringAssert.Contains(result, "No memories found");
    }

    [TestMethod]
    public async Task SearchMemory_EntryId_AppearsInBrackets()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("abc123", "Some fact", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "[abc123]");
    }

    // -------------------------------------------------------------------------
    // DeleteMemory
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task DeleteMemory_KnownId_ReturnsConfirmationWithContent()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Rocky has a dog named Milo", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.DeleteMemory("id1");

        StringAssert.Contains(result, "id1");
        StringAssert.Contains(result, "Rocky has a dog named Milo");
    }

    [TestMethod]
    public async Task DeleteMemory_KnownId_EntryIsRemoved()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "To be deleted", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        await tools.DeleteMemory("id1");

        Assert.IsNull(await memory.GetAsync("id1"));
    }

    [TestMethod]
    public async Task DeleteMemory_UnknownId_ReturnsNotFoundMessage()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.DeleteMemory("nonexistent");

        StringAssert.Contains(result, "No memory entry found");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MemoryTools MakeTools(StubLongTermMemory memory) =>
        new(memory, new StubChatClient(), Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()), NullLogger<MemoryTools>.Instance);

    private static MemoryEntry Entry(string id, string content, DateTimeOffset createdAt) =>
        new(id, content, null, [], createdAt);
}

// ---------------------------------------------------------------------------
// Stubs
// ---------------------------------------------------------------------------

/// <summary>
/// In-memory implementation of <see cref="ILongTermMemory"/> for tests.
/// <see cref="SearchAsync"/> returns all stored entries regardless of criteria
/// so tests can focus on output formatting rather than search logic.
/// </summary>
internal sealed class StubLongTermMemory : ILongTermMemory
{
    private readonly List<MemoryEntry> _entries = [];

    public void Add(MemoryEntry entry) => _entries.Add(entry);

    public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        _entries.RemoveAll(e => e.Id == entry.Id);
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries]);

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _entries.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Minimal <see cref="ILlmClient"/> stub. Returns an empty JSON array by default
/// so that <see cref="MemoryTools.SaveMemory"/> falls back to direct save gracefully.
/// Not called by SearchMemory or DeleteMemory.
/// </summary>
internal sealed class StubChatClient : ILlmClient
{
    public bool IsIdle => true;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetResponseAsync(messages, options, cancellationToken);
}
