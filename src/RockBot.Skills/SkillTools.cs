using System.ComponentModel;
using System.Security.Cryptography;
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
    private readonly ISkillResourceUsageStore? _resourceUsageStore;

    public SkillTools(
        ISkillStore skillStore,
        ILlmClient llmClient,
        ILogger logger,
        string? sessionId = null,
        ISkillUsageStore? usageStore = null,
        bool enablePromote = false,
        ISkillResourceUsageStore? resourceUsageStore = null)
    {
        _skillStore = skillStore;
        _llmClient = llmClient;
        _logger = logger;
        _sessionId = sessionId;
        _usageStore = usageStore;
        _resourceUsageStore = resourceUsageStore;

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetSkill),
            AIFunctionFactory.Create(GetSkillResource),
            AIFunctionFactory.Create(ListSkills),
            AIFunctionFactory.Create(SaveSkill),
            AIFunctionFactory.Create(EditSkill),
            AIFunctionFactory.Create(DeleteSkill),
        };

        // Promotion is currently a subagent-only path. Subagents are the part of the
        // system that performs the exploratory tool-call discovery whose result is
        // worth capturing as a typed asset; the main agent reaches assets via skills
        // the dream pass has already promoted.
        if (enablePromote)
            tools.Add(AIFunctionFactory.Create(PromoteSkillAsset));

        Tools = tools;
    }

    public IList<AITool> Tools { get; }

    [Description("Load the full instructions for a named skill so you can follow them. " +
                 "Call this when the skill index shows a skill relevant to the user's request. " +
                 "Returns the skill content and the list of sub-resources (if any) that can be " +
                 "fetched individually with get_skill_resource.")]
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

        var manifestBlock = FormatManifestBlock(name, skill.Manifest);
        if (manifestBlock.Length > 0)
            return skill.Content + "\n" + manifestBlock;

        return skill.Content;
    }

    /// <summary>
    /// Renders the "Resources" block listing attached assets for a skill, in the same
    /// format <see cref="GetSkill"/> appends to its response. Returns empty when the
    /// manifest is null or empty. Used by the auto-inject paths (BM25 rank-1 push) so
    /// the agent sees attached wisps/scripts without a get_skill round-trip.
    /// </summary>
    public static string FormatManifestBlock(string skillName, IReadOnlyList<SkillResource>? manifest)
    {
        if (manifest is null || manifest.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"**Resources** (fetch with `get_skill_resource(\"{skillName}\", \"<filename>\")`):");
        foreach (var entry in manifest)
            sb.AppendLine($"- `{entry.Filename}` ({entry.Type}): {entry.Description}");
        return sb.ToString().TrimEnd();
    }

    [Description("Fetch a single sub-resource file from a skill's resource folder. " +
                 "Use the manifest shown by get_skill to decide which resource to load.")]
    public async Task<string> GetSkillResource(
        [Description("The skill name (e.g. 'plan-meeting')")] string skillName,
        [Description("The filename of the resource to fetch (e.g. 'script.py', 'schema.json')")] string filename)
    {
        _logger.LogInformation("Tool call: GetSkillResource(skillName={Name}, filename={Filename})", skillName, filename);

        var skill = await _skillStore.GetAsync(skillName);
        if (skill is null)
            return $"Skill '{skillName}' not found. Call list_skills to see available skills.";

        var content = await _skillStore.GetResourceAsync(skillName, filename);
        if (content is null)
            return $"Resource '{filename}' not found in skill '{skillName}'. " +
                   "Call get_skill to see the list of available resources.";

        // Fire-and-forget checkout recording — soft validation signal for the
        // provisional validation pass (a full success/failure signal exists for
        // wisp resources via DefinitionHash cross-reference; checkouts are the
        // fallback for non-wisp resources like Python and JsonSchema).
        if (_resourceUsageStore is not null && _sessionId is not null)
        {
            _ = _resourceUsageStore.RecordCheckoutAsync(
                skillName, filename, _sessionId, DateTimeOffset.UtcNow);
        }

        return content;
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
                 "Attach structured artifacts (scripts, schemas, etc.) as resources rather than embedding them in markdown. " +
                 "A summary will be generated automatically and added to the skill index. " +
                 "Returns the updated skill index.")]
    public async Task<string> SaveSkill(
        [Description("Skill name — lowercase, hyphens allowed, forward slash for subcategories " +
                     "(e.g. 'plan-meeting', 'research/summarize')")] string name,
        [Description("Full skill content in markdown format")] string content,
        [Description("Optional list of sub-resource files to save alongside the skill. " +
                     "Each resource must supply filename, type, description, and content. " +
                     "Providing this list replaces all previously saved resources for this skill.")] IReadOnlyList<SkillResourceInput>? resources = null)
    {
        _logger.LogInformation("Tool call: SaveSkill(name={Name}, resourceCount={Count})", name, resources?.Count ?? 0);

        var now = DateTimeOffset.UtcNow;
        var existing = await _skillStore.GetAsync(name);

        // When resources is null the caller is only updating the markdown/metadata;
        // preserve the existing manifest so resource files on disk stay in sync.
        // When resources is explicitly provided the 2-arg SaveAsync rebuilds the manifest.
        var preservedManifest = resources is null ? existing?.Manifest : null;

        // Save immediately with empty summary; LLM generates it in the background
        var skill = new Skill(name, "", content, existing?.CreatedAt ?? now, now, LastUsedAt: now,
            Manifest: preservedManifest);
        await _skillStore.SaveAsync(skill, resources);

        _ = Task.Run(() => GenerateSummaryAsync(name, content));

        var index = await _skillStore.ListAsync();
        return $"Skill '{name}' saved. Summary is being generated.\n\n{FormatIndex(index)}";
    }

    [Description("Change part of an existing skill's markdown without rewriting the whole document. " +
                 "This is the right tool for the usual case — adding a pitfall you just hit, correcting a step, " +
                 "updating an example. save_skill replaces the entire body, so anything you do not reproduce " +
                 "verbatim is lost, and on a long procedure you will not reproduce it verbatim. " +
                 "Editing also keeps the existing summary instead of regenerating it. " +
                 "Call get_skill first and copy old_string from what it returns; it must match exactly, and if " +
                 "it appears more than once the edit is refused — include more surrounding text or set replace_all.")]
    public async Task<string> EditSkill(
        [Description("The skill name to edit (e.g. 'plan-meeting')")] string name,
        [Description("Exact text to find in the skill's markdown — copy it verbatim from get_skill")] string old_string,
        [Description("Replacement text. Pass an empty string to delete the matched text.")] string new_string,
        [Description("Replace every occurrence instead of refusing an ambiguous match. Default false.")] bool replace_all = false)
    {
        _logger.LogInformation("Tool call: EditSkill(name={Name}, replaceAll={ReplaceAll})", name, replace_all);

        var result = await _skillStore.EditContentAsync(
            name, old_string ?? string.Empty, new_string ?? string.Empty, replace_all);

        if (!result.IsSuccess)
            return $"Edit failed on skill '{name}': {result.Error}";

        var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
        return $"Skill '{name}' edited — replaced {result.ReplacementCount} {plural} " +
               $"({result.OldLength} → {result.NewLength} characters). Summary and resources unchanged.";
    }

    [Description("Save a working asset (wisp definition, script, schema) you just verified " +
                 "as a resource attached to the skill that guided you. Use this only after " +
                 "a tool-call sequence has actually executed successfully — never speculatively. " +
                 "The resource is marked provisional until validated by future runs. " +
                 "Promotion attaches to an existing skill — call save_skill first if no relevant " +
                 "skill exists yet.")]
    public async Task<string> PromoteSkillAsset(
        [Description("The skill that guided you to this working asset (must already exist).")] string skillName,
        [Description("Filename for the asset (e.g. 'fanout.json', 'compute.py'). Simple filename only — no path separators.")] string filename,
        [Description("The asset's type — Wisp for a wisp definition, Python for a script, JsonSchema for a schema, etc.")] SkillResourceType type,
        [Description("One-line description of what this asset does.")] string description,
        [Description("The exact body that just executed successfully — the wisp definition JSON, the script source, etc.")] string content,
        [Description("Optional advisory text describing how a future session would know this asset still works (e.g. 'returns per-account event arrays').")] string? verifyHint = null)
    {
        _logger.LogInformation(
            "Tool call: PromoteSkillAsset(skill={Skill}, filename={Filename}, type={Type})",
            skillName, filename, type);

        var existing = await _skillStore.GetAsync(skillName);
        if (existing is null)
            return $"Skill '{skillName}' not found. Promotion attaches to an existing skill; " +
                   "call save_skill first to create it, then promote the asset.";

        // Pre-build the manifest entry so Provisional, CreatedAt, VerifyHint, and
        // DefinitionHash are all set per the in-session-promotion contract.
        var input = new SkillResourceInput(
            filename, type, description, content,
            Provisional: true,
            VerifyHint: verifyHint);
        var entry = new SkillResource(
            filename, type, description,
            Provisional: true,
            CreatedAt: DateTimeOffset.UtcNow,
            VerifyHint: verifyHint,
            DefinitionHash: ComputeContentHash(content));

        var attached = await _skillStore.AttachResourceAsync(skillName, input, entry);
        if (!attached)
            return $"Skill '{skillName}' not found.";

        return $"Asset '{filename}' attached to skill '{skillName}' as provisional {type}. " +
               "It will be validated by future successful runs and demoted if it stops working.";
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

            // Detached background work: skill save queues this via Task.Run with no
            // caller-supplied ct, so the LLM call has no cancellation source. The
            // summary refresh is best-effort; if the agent shuts down mid-call the
            // task is orphaned. A future refactor could use ApplicationStopping.
            var response = await _llmClient.GetResponseAsync(
                messages, new ChatOptions(), CancellationToken.None);
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
            var resourceTag = FormatResourceTag(s.Manifest);
            sb.AppendLine($"- {s.Name} ({ageDays}d old, {lastUsedPart}){resourceTag}: {summary}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Produces a compact <c>[Wisp, Python]</c>-style tag listing the distinct resource types
    /// attached to a skill, or an empty string if the skill has no resources. Lets the LLM see
    /// at a glance (from the skill index) which skills already carry a saved wisp, script, etc.
    /// A trailing <c>*</c> on a type marks that at least one entry of that type is provisional —
    /// captured in-session and not yet validated by repeated successful use.
    /// </summary>
    public static string FormatResourceTag(IReadOnlyList<SkillResource>? manifest)
    {
        if (manifest is null || manifest.Count == 0)
            return "";

        var types = manifest
            .GroupBy(r => r.Type)
            .Select(g => new { Type = g.Key.ToString(), HasProvisional = g.Any(r => r.Provisional) })
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => x.HasProvisional ? x.Type + "*" : x.Type)
            .ToList();

        return $" [{string.Join(", ", types)}]";
    }

    /// <summary>
    /// SHA-256-hex16 of <paramref name="content"/> — same scheme as the wisp execution
    /// log's definition hash. Used by promotion so the validation pass can cross-reference
    /// resource bodies against recent wisp records.
    /// </summary>
    internal static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
