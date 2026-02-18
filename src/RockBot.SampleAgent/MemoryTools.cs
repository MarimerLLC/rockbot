using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.SampleAgent;

/// <summary>
/// LLM-callable tools for managing long-term agent memory.
/// Methods are discovered by <c>AIFunctionFactory.Create</c> and exposed to the LLM.
/// Registered as a singleton so <see cref="AIFunction"/> instances are built once.
/// </summary>
internal sealed class MemoryTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// System prompt used when asking the LLM to expand a raw memory string into
    /// one or more well-structured, keyword-rich entries.
    /// </summary>
    private const string ExtractionSystemPrompt = """
        You are a memory extraction assistant. Your job is to take a piece of information
        and expand it into one or more well-structured, easily searchable memory entries.

        Rules:
        - Split compound facts into separate entries — one distinct fact per entry.
        - Write each "content" field as a natural sentence that includes synonyms and
          related terms so keyword search is robust. Example: write
          "Rocky has a dog — a Sheltie (Shetland Sheepdog) named Milo" rather than
          just "Rocky has a Sheltie named Milo", so that searches for "dog", "pet",
          "sheltie", or "Milo" all match.
        - Choose specific but not overly deep category paths
          (e.g. "user-preferences/pets", not "user-preferences/animals/dogs/breeds").
        - Tags should be lowercase single words or short hyphenated phrases.
        - Caller-supplied category and tags are hints — use them as a starting point
          but improve or override them as needed.

        Return ONLY a valid JSON array of objects. No markdown, no explanation, no
        code fences — just the raw JSON array. Each object must have exactly these fields:
          "content"  : string
          "category" : string or null
          "tags"     : array of strings

        Example input:
          Content: "Rocky's wife is Teresa, has two adult children, and a Sheltie named Milo"
          Hints — category: user-preferences, tags: (none)

        Example output:
        [
          {"content":"Rocky's wife is named Teresa","category":"user-preferences/family","tags":["family","wife","spouse","Teresa"]},
          {"content":"Rocky has two adult children","category":"user-preferences/family","tags":["family","children","kids","adult-children"]},
          {"content":"Rocky has a dog — a Sheltie (Shetland Sheepdog) named Milo","category":"user-preferences/pets","tags":["pet","dog","sheltie","shetland-sheepdog","Milo"]}
        ]
        """;

    private readonly ILongTermMemory _memory;
    private readonly IChatClient _chatClient;
    private readonly ILogger<MemoryTools> _logger;
    private readonly IList<AITool> _tools;

    public MemoryTools(ILongTermMemory memory, IChatClient chatClient, ILogger<MemoryTools> logger)
    {
        _memory = memory;
        _chatClient = chatClient;
        _logger = logger;

        // Build AIFunction instances once at construction (singleton)
        _tools =
        [
            AIFunctionFactory.Create(SaveMemory),
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(DeleteMemory),
            AIFunctionFactory.Create(ListCategories)
        ];
    }

    /// <summary>
    /// Returns the <see cref="AIFunction"/> instances for use in <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IList<AITool> Tools => _tools;

    [Description("Save an important fact, user preference, or learned pattern to long-term memory. " +
                 "The system will automatically expand the content into one or more focused, keyword-rich entries " +
                 "to improve future search recall — so you can pass a natural-language description and trust " +
                 "that it will be stored well. Use category to give a starting hint for organization " +
                 "(e.g. 'user-preferences', 'project-context/rockbot'). Use tags for cross-cutting labels.")]
    public async Task<string> SaveMemory(
        [Description("The content to remember — can be a natural-language sentence or a compound fact")] string content,
        [Description("Optional category hint (e.g. 'user-preferences/pets')")] string? category = null,
        [Description("Optional comma-separated tags hint")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SaveMemory(content={Content}, category={Category}, tags={Tags})",
            content, category, tags);

        var entries = await ExpandToMemoryEntriesAsync(content, category, tags);

        var savedIds = new List<string>();
        foreach (var entry in entries)
        {
            await _memory.SaveAsync(entry);
            savedIds.Add(entry.Id);
            _logger.LogInformation("Saved memory entry {Id} ({Category}): {Content}",
                entry.Id, entry.Category ?? "(none)", entry.Content);
        }

        return entries.Count == 1
            ? $"Saved 1 memory entry (id={savedIds[0]})."
            : $"Saved {entries.Count} memory entries (ids={string.Join(", ", savedIds)}).";
    }

    [Description("Search long-term memory for previously saved facts, preferences, or patterns. " +
                 "Use query for keyword search and category for scoping to a knowledge area.")]
    public async Task<string> SearchMemory(
        [Description("Optional keyword to search for in memory content")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'user-preferences')")] string? category = null)
    {
        _logger.LogInformation("Tool call: SearchMemory(query={Query}, category={Category})", query, category);

        var criteria = new MemorySearchCriteria(
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim());

        var results = await _memory.SearchAsync(criteria);

        _logger.LogInformation("SearchMemory returned {Count} results", results.Count);

        if (results.Count == 0)
            return "No memories found matching the search criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memory entries:");
        sb.AppendLine();

        foreach (var entry in results)
        {
            var daysOld = (int)(DateTimeOffset.UtcNow - entry.CreatedAt).TotalDays;
            var age = daysOld == 0 ? "today" : daysOld == 1 ? "1 day ago" : $"{daysOld} days ago";
            sb.AppendLine($"- [{entry.Id}] ({entry.Category ?? "uncategorized"}, remembered {age}): {entry.Content}");
            if (entry.Tags.Count > 0)
                sb.AppendLine($"  Tags: {string.Join(", ", entry.Tags)}");
        }

        return sb.ToString();
    }

    [Description("Delete a memory entry by its ID. Use this to remove facts that are wrong, outdated, or " +
                 "superseded. Find the ID first by calling SearchMemory — IDs appear in brackets, e.g. [abc123]. " +
                 "To correct a wrong fact: delete the old entry, then save the corrected version with SaveMemory.")]
    public async Task<string> DeleteMemory(
        [Description("The ID of the memory entry to delete (from SearchMemory results)")] string id)
    {
        _logger.LogInformation("Tool call: DeleteMemory(id={Id})", id);

        var existing = await _memory.GetAsync(id);
        if (existing is null)
        {
            _logger.LogWarning("DeleteMemory: entry {Id} not found", id);
            return $"No memory entry found with id '{id}'. Use SearchMemory to find the correct ID.";
        }

        await _memory.DeleteAsync(id);

        _logger.LogInformation("Deleted memory entry {Id} ({Category}): {Content}",
            id, existing.Category ?? "(none)", existing.Content);

        return $"Deleted memory entry '{id}': \"{existing.Content}\"";
    }

    [Description("List all memory categories to see how knowledge is organized. " +
                 "Use this to discover what categories exist before searching.")]
    public async Task<string> ListCategories()
    {
        _logger.LogInformation("Tool call: ListCategories()");

        var categories = await _memory.ListCategoriesAsync();

        _logger.LogInformation("ListCategories returned {Count} categories", categories.Count);

        if (categories.Count == 0)
            return "No categories yet. Save a memory with a category to create one.";

        var sb = new StringBuilder();
        sb.AppendLine("Memory categories:");
        foreach (var cat in categories)
            sb.AppendLine($"- {cat}");

        return sb.ToString();
    }

    /// <summary>
    /// Calls the LLM to expand <paramref name="content"/> into one or more focused,
    /// keyword-rich <see cref="MemoryEntry"/> records. Falls back to a single direct
    /// save if the LLM call fails or returns unparseable output.
    /// </summary>
    private async Task<List<MemoryEntry>> ExpandToMemoryEntriesAsync(
        string content, string? category, string? tags)
    {
        var hint = new StringBuilder("Content: ").AppendLine(content);
        hint.Append("Hints — category: ").Append(category ?? "(none)");
        hint.Append(", tags: ").AppendLine(tags ?? "(none)");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ExtractionSystemPrompt),
            new(ChatRole.User, hint.ToString())
        };

        // Explicit options: no tools (avoids recursive SaveMemory calls), no special overrides
        var options = new ChatOptions();

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options);
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonArray(raw);

            _logger.LogDebug("Memory extraction LLM response (raw {RawLen} chars, json {JsonLen} chars): {Json}",
                raw.Length, json.Length, json);

            var dtos = JsonSerializer.Deserialize<List<MemoryEntryDto>>(json, JsonOptions);
            if (dtos is { Count: > 0 })
            {
                return dtos
                    .Where(d => !string.IsNullOrWhiteSpace(d.Content))
                    .Select(d => new MemoryEntry(
                        Id: Guid.NewGuid().ToString("N")[..12],
                        Content: d.Content.Trim(),
                        Category: string.IsNullOrWhiteSpace(d.Category) ? null : d.Category.Trim(),
                        Tags: d.Tags ?? [],
                        CreatedAt: DateTimeOffset.UtcNow))
                    .ToList();
            }

            _logger.LogWarning("Memory extraction returned empty or null list; falling back");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction LLM call failed; falling back to direct save");
        }

        // Fallback: save the raw content as-is
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? (IReadOnlyList<string>)[]
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [
            new MemoryEntry(
                Id: Guid.NewGuid().ToString("N")[..12],
                Content: content,
                Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                Tags: tagList,
                CreatedAt: DateTimeOffset.UtcNow)
        ];
    }

    /// <summary>
    /// Extracts the outermost JSON array from <paramref name="text"/>, tolerating:
    /// <list type="bullet">
    ///   <item>Markdown code fences (```json … ```)</item>
    ///   <item>DeepSeek-style thinking blocks (&lt;think&gt;…&lt;/think&gt;) before the JSON</item>
    ///   <item>Prose preamble or trailing commentary outside the array brackets</item>
    /// </list>
    /// Returns an empty string if no array is found.
    /// </summary>
    private static string ExtractJsonArray(string text)
    {
        // Strip <think>...</think> blocks first (DeepSeek reasoning preamble)
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0 && thinkEnd > thinkStart)
            text = text[(thinkEnd + "</think>".Length)..].TrimStart();

        // Find the outermost [ ... ] span
        var arrayStart = text.IndexOf('[');
        var arrayEnd = text.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
            return text[arrayStart..(arrayEnd + 1)];

        return string.Empty;
    }

    private sealed record MemoryEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags);
}
