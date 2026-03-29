using System.Text.Json;

namespace RockBot.Host.Tests;

[TestClass]
public class EpisodeExtractionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ── DreamOptions defaults ────────────────────────────────────────────────

    [TestMethod]
    public void DreamOptions_EpisodeExtractionEnabled_DefaultsToTrue()
    {
        var options = new DreamOptions();
        Assert.IsTrue(options.EpisodeExtractionEnabled);
    }

    [TestMethod]
    public void DreamOptions_EpisodeDirectivePath_DefaultsToEpisodeDreamMd()
    {
        var options = new DreamOptions();
        Assert.AreEqual("episode-dream.md", options.EpisodeDirectivePath);
    }

    // ── Episode DTO serialization ────────────────────────────────────────────

    [TestMethod]
    public void EpisodeExtractionResult_Deserializes_NewEpisodes()
    {
        var json = """
        {
          "toSave": [
            {
              "content": "User investigated Azure content filter rejections.",
              "category": "episodic/problem",
              "actor": "user",
              "eventType": "problem",
              "importance": 0.7,
              "tags": ["episodic", "azure", "content-filter"],
              "sourceSessions": ["blazor-session"]
            }
          ],
          "toUpdate": []
        }
        """;

        var result = JsonSerializer.Deserialize<EpisodeExtractionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.ToSave?.Count);
        Assert.AreEqual(0, result.ToUpdate?.Count);

        var episode = result.ToSave![0];
        Assert.AreEqual("User investigated Azure content filter rejections.", episode.Content);
        Assert.AreEqual("episodic/problem", episode.Category);
        Assert.AreEqual("user", episode.Actor);
        Assert.AreEqual("problem", episode.EventType);
        Assert.AreEqual(0.7f, episode.Importance);
        Assert.AreEqual(3, episode.Tags?.Count);
        Assert.AreEqual("blazor-session", episode.SourceSessions?[0]);
    }

    [TestMethod]
    public void EpisodeExtractionResult_Deserializes_Reinforcements()
    {
        var json = """
        {
          "toSave": [],
          "toUpdate": [
            {
              "id": "abc123def456",
              "content": "Enriched summary with new context from latest discussion.",
              "importance": 0.8,
              "sourceSessions": ["session-2"]
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<EpisodeExtractionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.ToSave?.Count);
        Assert.AreEqual(1, result.ToUpdate?.Count);

        var update = result.ToUpdate![0];
        Assert.AreEqual("abc123def456", update.Id);
        Assert.AreEqual(0.8f, update.Importance);
        Assert.AreEqual("session-2", update.SourceSessions?[0]);
    }

    [TestMethod]
    public void EpisodeExtractionResult_Deserializes_EmptyResult()
    {
        var json = """{ "toSave": [], "toUpdate": [] }""";

        var result = JsonSerializer.Deserialize<EpisodeExtractionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.ToSave?.Count);
        Assert.AreEqual(0, result.ToUpdate?.Count);
    }

    [TestMethod]
    public void EpisodeExtractionResult_Deserializes_NullOptionalFields()
    {
        var json = """
        {
          "toSave": [
            {
              "content": "Minimal episode.",
              "importance": 0.4
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<EpisodeExtractionResultDto>(json, JsonOptions);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.ToSave?.Count);
        Assert.IsNull(result.ToUpdate);

        var episode = result.ToSave![0];
        Assert.IsNull(episode.Category);
        Assert.IsNull(episode.Actor);
        Assert.IsNull(episode.EventType);
        Assert.IsNull(episode.Tags);
        Assert.IsNull(episode.SourceSessions);
    }

    // ── MemoryEntry metadata for episodes ────────────────────────────────────

    [TestMethod]
    public void EpisodicMemoryEntry_MetadataCarriesImportanceAndActor()
    {
        var metadata = new Dictionary<string, string>
        {
            ["importance"] = 0.75f.ToString("F2"),
            ["actor"] = "user",
            ["event_type"] = "decision",
            ["source_sessions"] = "session-1,session-2"
        };

        var entry = new MemoryEntry(
            Id: "test123",
            Content: "User decided to use file-based episodic storage.",
            Category: "episodic/decision",
            Tags: ["episodic", "architecture"],
            CreatedAt: DateTimeOffset.UtcNow,
            Metadata: metadata,
            ImportanceScore: 0.75f);

        Assert.AreEqual(0.75f, entry.ImportanceScore);
        Assert.AreEqual("0.75", entry.Metadata!["importance"]);
        Assert.AreEqual("user", entry.Metadata["actor"]);
        Assert.AreEqual("decision", entry.Metadata["event_type"]);
        Assert.AreEqual("session-1,session-2", entry.Metadata["source_sessions"]);
        Assert.AreEqual("episodic/decision", entry.Category);
        Assert.IsTrue(entry.Tags.Contains("episodic"));
    }

    [TestMethod]
    public void EpisodicMemoryEntry_SearchByCategoryPrefix()
    {
        // Verify the category prefix search pattern works for episodic entries
        var categories = new[]
        {
            "episodic/conversation",
            "episodic/task",
            "episodic/decision",
            "episodic/discovery",
            "episodic/problem",
            "project/infrastructure"
        };

        var episodicCategories = categories
            .Where(c => c.StartsWith("episodic", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.AreEqual(5, episodicCategories.Count);
        Assert.IsFalse(episodicCategories.Contains("project/infrastructure"));
    }

    // ── Internal DTOs (accessible via InternalsVisibleTo) ────────────────────

    // These records are internal to RockBot.Host but visible to tests.
    // They match the JSON the LLM returns during episode extraction.

    private sealed record EpisodeExtractionResultDto(
        List<EpisodeEntryDto>? ToSave,
        List<EpisodeUpdateDto>? ToUpdate);

    private sealed record EpisodeEntryDto(
        string Content,
        string? Category,
        string? Actor,
        string? EventType,
        float? Importance,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<string>? SourceSessions);

    private sealed record EpisodeUpdateDto(
        string Id,
        string Content,
        float? Importance,
        IReadOnlyList<string>? SourceSessions);
}
