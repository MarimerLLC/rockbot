using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Skills;

/// <summary>
/// LLM-callable tools for managing agent skills — named markdown procedure documents
/// the agent can create, consult, and refine over time.
///
/// Instantiated per-session so the session ID is baked in at construction time,
/// enabling fire-and-forget skill invocation tracking.
/// Background LLM calls generate summaries for newly saved skills, mirroring the memory
/// enrichment pattern in <see cref="MemoryTools"/>.
/// </summary>
public sealed class SkillTools
{
    private const string SummarySystemPrompt =
        """
        You are summarizing an agent skill document.
        Write a single concise sentence of 15 words or fewer that describes what this skill
        does and when to use it. Return only the sentence — no quotes, no punctuation at the end.
        """;

    private readonly ISkillStore _skillStore;
    private readonly ILlmClient _llmClient;
    private readonly ILogger _logger;
    private readonly string? _sessionId;
    private readonly ISkillUsageStore? _usageStore;

    public SkillTools(
        ISkillStore skillStore,
        ILlmClient llmClient,
        ILogger logger,
        string? sessionId = null,
        ISkillUsageStore? usageStore = null)
    {
        _skillStore = skillStore;
        _llmClient = llmClient;
        _logger = logger;
        _sessionId = sessionId;
        _usageStore = usageStore;

        Tools =
        [
            AIFunctionFactory.Create(GetSkill),
            AIFunctionFactory.Create(ListSkills),
            AIFunctionFactory.Create(SaveSkill),
            AIFunctionFactory.Create(DeleteSkill)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("Load the full instructions for a named skill so you can follow them. " +
                 "Call this when the skill index shows a skill relevant to the user's request.")]
    public async Task<string> GetSkill(
        [Description("The skill name as shown in the index (e.g. 'plan-meeting')")] string name)
    {
        _logger.LogInformation("Tool call: GetSkill(name={Name})", name);

        var skill = await _skillStore.GetAsync(name);
        if (skill is null)
            return $"Skill '{name}' not found. Call list_skills to see available skills.";

        await _skillStore.SaveAsync(skill with { LastUsedAt = DateTimeOffset.UtcNow });

        // Fire-and-forget usage tracking — no latency impact
        if (_usageStore is not null && _sessionId is not null)
        {
            _ = _usageStore.AppendAsync(new SkillInvocationEvent(
                Id: Guid.NewGuid().ToString("N")[..12],
                SkillName: name,
                SessionId: _sessionId,
                Timestamp: DateTimeOffset.UtcNow));
        }

        return skill.Content;
    }

    [Description("List all available skills with their one-line summaries. " +
                 "Use this to discover what skills exist or to refresh the index mid-session.")]
    public async Task<string> ListSkills()
    {
        _logger.LogInformation("Tool call: ListSkills()");

        var skills = await _skillStore.ListAsync();
        return FormatIndex(skills);
    }

    [Description("Create or update a skill with markdown instructions for completing a specific type of task. " +
                 "Write the content as markdown: include a heading, a 'When to use' section, and numbered steps. " +
                 "A summary will be generated automatically and added to the skill index. " +
                 "Returns the updated skill index.")]
    public async Task<string> SaveSkill(
        [Description("Skill name — lowercase, hyphens allowed, forward slash for subcategories " +
                     "(e.g. 'plan-meeting', 'research/summarize')")] string name,
        [Description("Full skill content in markdown format")] string content)
    {
        _logger.LogInformation("Tool call: SaveSkill(name={Name})", name);

        var now = DateTimeOffset.UtcNow;
        var existing = await _skillStore.GetAsync(name);

        // Save immediately with empty summary; LLM generates it in the background
        var skill = new Skill(name, "", content, existing?.CreatedAt ?? now, now);
        await _skillStore.SaveAsync(skill);

        _ = Task.Run(() => GenerateSummaryAsync(name, content));

        var index = await _skillStore.ListAsync();
        return $"Skill '{name}' saved. Summary is being generated.\n\n{FormatIndex(index)}";
    }

    [Description("Delete a skill by name. Returns the updated skill index.")]
    public async Task<string> DeleteSkill(
        [Description("The skill name to delete")] string name)
    {
        _logger.LogInformation("Tool call: DeleteSkill(name={Name})", name);

        var existing = await _skillStore.GetAsync(name);
        if (existing is null)
            return $"Skill '{name}' not found. Call list_skills to see available skills.";

        await _skillStore.DeleteAsync(name);

        var index = await _skillStore.ListAsync();
        return $"Skill '{name}' deleted.\n\n{FormatIndex(index)}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls the LLM to generate a one-line summary for a newly saved skill,
    /// then updates the stored skill with that summary.
    /// </summary>
    private async Task GenerateSummaryAsync(string name, string content)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SummarySystemPrompt),
                new(ChatRole.User, content)
            };

            var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
            var summary = response.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.LogWarning("Summary generation returned empty result for skill '{Name}'", name);
                return;
            }

            // Re-fetch to get the latest saved version, then update the summary
            var current = await _skillStore.GetAsync(name);
            if (current is null)
            {
                _logger.LogWarning("Skill '{Name}' was deleted before summary could be applied", name);
                return;
            }

            await _skillStore.SaveAsync(current with { Summary = summary, UpdatedAt = DateTimeOffset.UtcNow });

            _logger.LogInformation("Generated summary for skill '{Name}': {Summary}", name, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary generation failed for skill '{Name}'", name);
        }
    }

    /// <summary>Formats the skill list as the index block shown to the LLM.</summary>
    public static string FormatIndex(IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0)
            return "No skills saved yet.";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Available skills ({skills.Count}):");
        foreach (var s in skills)
        {
            var summary = string.IsNullOrWhiteSpace(s.Summary) ? "(summary pending)" : s.Summary;
            var ageDays = (int)(now - s.CreatedAt).TotalDays;
            var lastUsedPart = s.LastUsedAt.HasValue
                ? $"last used {(int)(now - s.LastUsedAt.Value).TotalDays}d ago"
                : "never used";
            sb.AppendLine($"- {s.Name} ({ageDays}d old, {lastUsedPart}): {summary}");
        }
        return sb.ToString().TrimEnd();
    }
}
