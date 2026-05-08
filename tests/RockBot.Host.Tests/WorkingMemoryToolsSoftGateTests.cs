using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Memory;

namespace RockBot.Host.Tests;

[TestClass]
public class WorkingMemoryToolsSoftGateTests
{
    [TestMethod]
    public async Task SaveToWorkingMemory_BenignContent_DoesNotAddObservationTag()
    {
        var store = new RecordingWorkingMemory();
        var tools = NewTools(store);

        var result = await tools.SaveToWorkingMemory("note", "user prefers concise updates");

        Assert.IsNull(store.LastTags);
        Assert.AreEqual("Saved to working memory under key 'session/test/note'.", result);
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_ClaimLanguage_AugmentsTagsAndIncludesHint()
    {
        var store = new RecordingWorkingMemory();
        var tools = NewTools(store);

        var result = await tools.SaveToWorkingMemory(
            key: "calendar-blocked",
            data: "the calendar wrapper is blocked from passing arguments");

        Assert.IsNotNull(store.LastTags);
        CollectionAssert.Contains((System.Collections.ICollection)store.LastTags!, ObservationLanguageDetector.ObservationTag);
        StringAssert.Contains(result, "looks like a capability claim");
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_ClaimLanguageWithUserTags_PreservesUserTags()
    {
        var store = new RecordingWorkingMemory();
        var tools = NewTools(store);

        await tools.SaveToWorkingMemory(
            key: "calendar-blocked",
            data: "calendar cannot pass arguments",
            tags: "calendar,urgent");

        Assert.IsNotNull(store.LastTags);
        CollectionAssert.Contains((System.Collections.ICollection)store.LastTags!, "calendar");
        CollectionAssert.Contains((System.Collections.ICollection)store.LastTags!, "urgent");
        CollectionAssert.Contains((System.Collections.ICollection)store.LastTags!, ObservationLanguageDetector.ObservationTag);
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_AlreadyTaggedAsObservation_DoesNotDoubleTag()
    {
        var store = new RecordingWorkingMemory();
        var tools = NewTools(store);

        await tools.SaveToWorkingMemory(
            key: "calendar-blocked",
            data: "wrapper limitation observed",
            tags: $"existing,{ObservationLanguageDetector.ObservationTag}");

        Assert.IsNotNull(store.LastTags);
        var observationTagCount = store.LastTags!.Count(t =>
            string.Equals(t, ObservationLanguageDetector.ObservationTag, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, observationTagCount, "Observation tag must not be applied twice.");
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_BenignContent_NoHintInResult()
    {
        var store = new RecordingWorkingMemory();
        var tools = NewTools(store);

        var result = await tools.SaveToWorkingMemory("note", "standup notes from today");

        Assert.IsFalse(result.Contains("capability claim"),
            "Benign content must not produce a soft-gate hint.");
    }

    private static WorkingMemoryTools NewTools(IWorkingMemory store) =>
        new(store, "session/test", NullLogger.Instance);

    private sealed class RecordingWorkingMemory : IWorkingMemory
    {
        public IReadOnlyList<string>? LastTags { get; private set; }
        public string? LastKey { get; private set; }

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            LastKey = key;
            LastTags = tags;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}
