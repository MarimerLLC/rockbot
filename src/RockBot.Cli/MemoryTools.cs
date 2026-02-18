using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Cli;

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
    /// Extraction-specific instructions appended after the shared memory-rules.md content.
    /// Covers only what is unique to the save/enrich scenario.
    /// </summary>
    private const string ExtractionSpecificPrompt = """
        You are a memory extraction assistant. Your job is to take a piece of information
        and expand it into one or more well-structured, easily searchable memory entries,
        following the memory rules above.

        Additional rules for extraction:
        - Split compound facts into separate entries — one distinct fact per entry.
        - Caller-supplied category and tags are hints — use them as a starting point
          but improve or override them as needed.

        Return ONLY a valid JSON array of objects. No markdown, no explanation, no
        code fences — just the raw JSON array. Each object must have exactly these fields:
          "content"  : string
          "category" : string or null
          "tags"     : array of strings

        If the content contains nothing worth saving as a durable memory, return: []
        """;

    private readonly ILongTermMemory _memory;
    private readonly ILlmClient _llmClient;
    private readonly ILogger<MemoryTools> _logger;
    private readonly IList<AITool> _tools;
    private readonly string _extractionSystemPrompt;

    public MemoryTools(
        ILongTermMemory memory,
        ILlmClient llmClient,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<MemoryTools> logger)
    {
        _memory = memory;
        _llmClient = llmClient;
        _logger = logger;

        // Load shared memory rules and prepend to the extraction prompt
        var rulesPath = ResolvePath("memory-rules.md", profileOptions.Value.BasePath);
        var memoryRules = File.Exists(rulesPath) ? File.ReadAllText(rulesPath) : string.Empty;
        _extractionSystemPrompt = string.IsNullOrEmpty(memoryRules)
            ? ExtractionSpecificPrompt
            : memoryRules + "\n\n---\n\n" + ExtractionSpecificPrompt;

        // Build AIFunction instances once at construction (singleton)
        _tools =
        [
            AIFunctionFactory.Create(SaveMemory),
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(DeleteMemory),
            AIFunctionFactory.Create(ListCategories)
        ];
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path)) return path;
        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);
        return Path.Combine(baseDir, path);
    }

    /// <summary>
    /// Returns the <see cref="AIFunction"/> instances for use in <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IList<AITool> Tools => _tools;

    [Description("Save an important fact, user preference, or learned pattern to long-term memory. " +
                 "Returns immediately — the actual enrichment happens in the background. " +
                 "The system will automatically expand the content into focused, keyword-rich entries. " +
                 "Pass a natural-language description; no pre-structuring needed.")]
    public Task<string> SaveMemory(
        [Description("The content to remember — can be a natural-language sentence or a compound fact")] string content,
        [Description("Optional category hint (e.g. 'user-preferences/pets')")] string? category = null,
        [Description("Optional comma-separated tags hint")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SaveMemory (queuing background task) content={Content}, category={Category}",
            content, category);

        _ = Task.Run(() => SaveMemoryBackgroundAsync(content, category, tags));

        return Task.FromResult("Memory save queued.");
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
    /// Background worker for <see cref="SaveMemory"/>. Calls the LLM to enrich
    /// the raw content into well-structured entries, then persists them.
    /// </summary>
    private async Task SaveMemoryBackgroundAsync(string content, string? category, string? tags)
    {
        try
        {
            var entries = await ExpandToMemoryEntriesAsync(content, category, tags);

            foreach (var entry in entries)
            {
                await _memory.SaveAsync(entry);
                _logger.LogInformation("Background save: {Id} ({Category}): {Content}",
                    entry.Id, entry.Category ?? "(none)", entry.Content);
            }

            _logger.LogInformation("Background SaveMemory complete: {Count} new entries saved for content '{Content}'",
                entries.Count, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background SaveMemory failed for content: {Content}", content);
        }
    }

    /// <summary>
    /// Calls the LLM to expand <paramref name="content"/> into one or more focused,
    /// keyword-rich <see cref="MemoryEntry"/> records. Falls back to a single direct
    /// save if the LLM call fails or returns unparseable output.
    /// </summary>
    private async Task<List<MemoryEntry>> ExpandToMemoryEntriesAsync(
        string content, string? category, string? tags)
    {
        var userMessage = new StringBuilder();
        userMessage.Append("Content: ").AppendLine(content);
        userMessage.Append("Hints — category: ").Append(category ?? "(none)");
        userMessage.Append(", tags: ").AppendLine(tags ?? "(none)");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _extractionSystemPrompt),
            new(ChatRole.User, userMessage.ToString())
        };

        // Explicit options: no tools (avoids recursive SaveMemory calls)
        var options = new ChatOptions();

        try
        {
            var response = await _llmClient.GetResponseAsync(messages, options);
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

            if (json == "[]")
            {
                _logger.LogInformation("Memory extraction: LLM returned empty array for content");
                return [];
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
