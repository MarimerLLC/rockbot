using System.ComponentModel;
using System.Text;
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
    private readonly ILongTermMemory _memory;
    private readonly ILogger<MemoryTools> _logger;
    private readonly IList<AITool> _tools;

    public MemoryTools(ILongTermMemory memory, ILogger<MemoryTools> logger)
    {
        _memory = memory;
        _logger = logger;

        // Build AIFunction instances once at construction (singleton)
        _tools =
        [
            AIFunctionFactory.Create(SaveMemory),
            AIFunctionFactory.Create(SearchMemory),
            AIFunctionFactory.Create(ListCategories)
        ];
    }

    /// <summary>
    /// Returns the <see cref="AIFunction"/> instances for use in <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IList<AITool> Tools => _tools;

    [Description("Save an important fact, user preference, or learned pattern to long-term memory. " +
                 "Use category to organize knowledge hierarchically (e.g. 'user-preferences', 'project-context/rockbot'). " +
                 "Use tags for cross-cutting labels that aid search.")]
    public async Task<string> SaveMemory(
        [Description("The content to remember")] string content,
        [Description("Optional category path for organization (e.g. 'user-preferences', 'project-context/rockbot')")] string? category = null,
        [Description("Optional comma-separated tags for search filtering")] string? tags = null)
    {
        _logger.LogInformation("Tool call: save_memory(content={Content}, category={Category}, tags={Tags})",
            content, category, tags);

        var id = Guid.NewGuid().ToString("N")[..12];
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? (IReadOnlyList<string>)[]
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var entry = new MemoryEntry(
            id,
            content,
            string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            tagList,
            DateTimeOffset.UtcNow);

        await _memory.SaveAsync(entry);

        _logger.LogInformation("Saved memory entry {Id} in category '{Category}'", id, entry.Category ?? "(none)");
        return $"Saved memory '{id}' in category '{entry.Category ?? "(none)"}'.";
    }

    [Description("Search long-term memory for previously saved facts, preferences, or patterns. " +
                 "Use query for keyword search and category for scoping to a knowledge area.")]
    public async Task<string> SearchMemory(
        [Description("Optional keyword to search for in memory content")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'user-preferences')")] string? category = null)
    {
        _logger.LogInformation("Tool call: search_memory(query={Query}, category={Category})", query, category);

        var criteria = new MemorySearchCriteria(
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim());

        var results = await _memory.SearchAsync(criteria);

        _logger.LogInformation("search_memory returned {Count} results", results.Count);

        if (results.Count == 0)
            return "No memories found matching the search criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memory entries:");
        sb.AppendLine();

        foreach (var entry in results)
        {
            sb.AppendLine($"- [{entry.Id}] ({entry.Category ?? "uncategorized"}): {entry.Content}");
            if (entry.Tags.Count > 0)
                sb.AppendLine($"  Tags: {string.Join(", ", entry.Tags)}");
        }

        return sb.ToString();
    }

    [Description("List all memory categories to see how knowledge is organized. " +
                 "Use this to discover what categories exist before searching.")]
    public async Task<string> ListCategories()
    {
        _logger.LogInformation("Tool call: list_categories()");

        var categories = await _memory.ListCategoriesAsync();

        _logger.LogInformation("list_categories returned {Count} categories", categories.Count);

        if (categories.Count == 0)
            return "No categories yet. Save a memory with a category to create one.";

        var sb = new StringBuilder();
        sb.AppendLine("Memory categories:");
        foreach (var cat in categories)
            sb.AppendLine($"- {cat}");

        return sb.ToString();
    }
}
