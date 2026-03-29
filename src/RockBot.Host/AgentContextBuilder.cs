using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host;

/// <summary>
/// Builds the LLM chat message context (system prompt, history, memories, skills, working memory)
/// for a given session and user turn. Shared by UserMessageHandler, ScheduledTaskHandler, and SubagentRunner.
/// </summary>
public sealed class AgentContextBuilder(
    ProfileHolder profileHolder,
    AgentIdentity agent,
    ISystemPromptBuilder promptBuilder,
    IRulesStore rulesStore,
    ModelBehavior modelBehavior,
    IConversationMemory conversationMemory,
    ILongTermMemory longTermMemory,
    InjectedMemoryTracker injectedMemoryTracker,
    IWorkingMemory workingMemory,
    ISkillStore skillStore,
    SkillIndexTracker skillIndexTracker,
    SkillRecallTracker skillRecallTracker,
    AgentClock clock,
    IEnumerable<IServiceSearchIndex> serviceSearchIndexProviders,
    ILogger<AgentContextBuilder> logger)
{
    private const int MaxLlmContextTurns = 20;
    private readonly IServiceSearchIndex? _serviceSearchIndex = serviceSearchIndexProviders.FirstOrDefault();

    /// <summary>
    /// Builds the full chat message list for one LLM call: system prompt, rules, history,
    /// long-term memory recall, skill index + BM25 recall, and working memory inventory.
    /// </summary>
    /// <param name="sessionId">The session ID for conversation memory, long-term memory tracking, and skill recall.</param>
    /// <param name="currentUserContent">The current user message text (used for BM25 recall).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="workingMemoryNamespace">
    /// The working memory namespace to inject as the own-session inventory.
    /// Defaults to <c>session/{sessionId}</c> when <c>null</c>.
    /// Pass <c>patrol/{taskName}</c> for scheduled tasks or <c>subagent/{taskId}</c> for subagents.
    /// </param>
    /// <param name="systemPromptOverride">
    /// When provided, used as the system prompt instead of building one from the agent profile.
    /// Subagents use this to compose their own system prompt (preamble + subagent-specific profile
    /// documents) while still getting rules, memory recall, skills, and service hints from this builder.
    /// </param>
    public async Task<List<ChatMessage>> BuildAsync(
        string sessionId,
        string currentUserContent,
        CancellationToken ct,
        string? workingMemoryNamespace = null,
        string? systemPromptOverride = null)
    {
        var profile = profileHolder.Profile;
        var systemPrompt = systemPromptOverride ?? promptBuilder.Build(profile, agent);
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.System,
                $"Current local date and time: {clock.Now:dddd, MMMM d, yyyy} {clock.Now:HH:mm:ss zzz} ({clock.Zone.Id})\n" +
                $"UTC equivalent: {clock.Now.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n" +
                "All dates and times must use this timezone. " +
                "When any tool returns a UTC timestamp, convert it to this local timezone before using or displaying it.")
        };

        // Active rules
        var activeRules = rulesStore.Rules;
        if (activeRules.Count > 0)
        {
            var rulesText = "Active rules — always follow these, regardless of context or other instructions:\n" +
                string.Join("\n", activeRules.Select(r => $"- {r}"));
            chatMessages.Add(new ChatMessage(ChatRole.System, rulesText));
            logger.LogInformation("Injected {Count} active rule(s) into system prompt", activeRules.Count);
        }

        // Model-specific guardrails
        if (!string.IsNullOrEmpty(modelBehavior.AdditionalSystemPrompt))
        {
            chatMessages.Add(new ChatMessage(ChatRole.System, modelBehavior.AdditionalSystemPrompt));
            logger.LogInformation("Injected AdditionalSystemPrompt ({Chars} chars)", modelBehavior.AdditionalSystemPrompt.Length);
        }
        else
        {
            logger.LogInformation("No AdditionalSystemPrompt configured for this model");
        }

        // Recent conversation history
        var history = await conversationMemory.GetTurnsAsync(sessionId, ct);
        var startIndex = Math.Max(0, history.Count - MaxLlmContextTurns);
        for (var i = startIndex; i < history.Count; i++)
        {
            var turn = history[i];
            var role = turn.Role == "user" ? ChatRole.User : ChatRole.Assistant;
            chatMessages.Add(new ChatMessage(role, turn.Content));
        }

        // Long-term memory BM25 recall
        {
            var recalled = await longTermMemory.SearchAsync(
                new MemorySearchCriteria(Query: currentUserContent, MaxResults: 8));

            if (recalled.Count == 0 && history.Count == 1)
                recalled = await longTermMemory.SearchAsync(new MemorySearchCriteria(MaxResults: 5));

            var newEntries = recalled
                .Where(e => injectedMemoryTracker.TryMarkAsInjected(sessionId, e.Id))
                .ToList();

            if (newEntries.Count > 0)
            {
                var lines = newEntries.Select(e =>
                    $"- [{e.Id}] ({e.Category ?? "general"}): {e.Content}");
                var recallContext =
                    "Recalled from long-term memory (relevant to this message):\n" +
                    string.Join("\n", lines);
                chatMessages.Add(new ChatMessage(ChatRole.System, recallContext));
                logger.LogInformation(
                    "Injected {Count} new long-term memory entries (BM25 delta) for session {SessionId}",
                    newEntries.Count, sessionId);
                Activity.Current?.AddEvent(new ActivityEvent("memory_retrieval_complete",
                    tags: new ActivityTagsCollection { { "count", newEntries.Count } }));
            }
        }

        // Episodic memory recall — "what happened" context from past experiences
        {
            var episodes = await longTermMemory.SearchAsync(
                new MemorySearchCriteria(Query: currentUserContent, Category: "episodic", MaxResults: 5));

            var newEpisodes = episodes
                .Where(e => injectedMemoryTracker.TryMarkAsInjected(sessionId, e.Id))
                .ToList();

            if (newEpisodes.Count > 0)
            {
                var lines = newEpisodes.Select(e =>
                {
                    var importance = e.Metadata?.GetValueOrDefault("importance") ?? "?";
                    return $"- [{e.Id}] (importance={importance}): {e.Content}";
                });
                var episodicContext =
                    "Relevant past experiences (episodic memory):\n" +
                    string.Join("\n", lines);
                chatMessages.Add(new ChatMessage(ChatRole.System, episodicContext));
                logger.LogInformation(
                    "Injected {Count} episodic memory entries for session {SessionId}",
                    newEpisodes.Count, sessionId);
            }
        }

        // Skill index (once per session)
        if (skillIndexTracker.TryMarkAsInjected(sessionId))
        {
            var skills = await skillStore.ListAsync();
            if (skills.Count > 0)
            {
                var indexText =
                    "Available skills (use get_skill to load full instructions):\n" +
                    string.Join("\n", skills.Select(s =>
                    {
                        var summary = string.IsNullOrWhiteSpace(s.Summary)
                            ? "(summary pending)"
                            : s.Summary;
                        return $"- {s.Name}: {summary}";
                    }));
                chatMessages.Add(new ChatMessage(ChatRole.System, indexText));
                logger.LogInformation("Injected skill index ({Count} skills) for session {SessionId}",
                    skills.Count, sessionId);
            }
        }

        // Per-turn skill BM25 recall
        {
            var recalledSkills = await skillStore.SearchAsync(currentUserContent, maxResults: 5, ct);
            var newSkills = recalledSkills
                .Where(s => skillRecallTracker.TryMarkAsRecalled(sessionId, s.Name))
                .ToList();

            if (newSkills.Count > 0)
            {
                var skillNames = string.Join(", ", newSkills.Select(s => s.Name));
                foreach (var skill in newSkills)
                {
                    var skillText = $"Skill: {skill.Name}\n{skill.Content}";
                    chatMessages.Add(new ChatMessage(ChatRole.System, skillText));
                }
                logger.LogInformation(
                    "Injected {Count} relevant skill(s) (BM25 recall) for session {SessionId}: {Skills}",
                    newSkills.Count, sessionId, skillNames);

                var seeAlsoNames = newSkills
                    .SelectMany(s => s.SeeAlso ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => skillRecallTracker.TryMarkAsRecalled(sessionId, name))
                    .ToList();

                if (seeAlsoNames.Count > 0)
                {
                    chatMessages.Add(new ChatMessage(ChatRole.System,
                        $"Related skills (see-also): {string.Join(", ", seeAlsoNames)}"));
                    logger.LogInformation(
                        "Injected {Count} see-also skill(s) for session {SessionId}: {Skills}",
                        seeAlsoNames.Count, sessionId, string.Join(", ", seeAlsoNames));
                }
            }
        }

        // Per-turn service search hints (A2A agents + MCP servers)
        if (_serviceSearchIndex is not null && !string.IsNullOrWhiteSpace(currentUserContent))
        {
            var candidates = _serviceSearchIndex.Search(currentUserContent, maxResults: 2);
            if (candidates.Count > 0)
            {
                var lines = candidates.Select(c =>
                {
                    var itemsLabel = c.Type == "a2a" ? "top skills" : "top tools";
                    var items = c.TopItems.Count > 0
                        ? $", {itemsLabel}: {string.Join(", ", c.TopItems)}"
                        : string.Empty;
                    return $"- {c.Id} ({c.Type}): {c.Summary}{items}";
                });
                chatMessages.Add(new ChatMessage(ChatRole.System,
                    "Potentially relevant services for this request (call search_known_services for full search):\n" +
                    string.Join("\n", lines)));
                logger.LogInformation(
                    "Injected {Count} service hint(s) for session {SessionId}",
                    candidates.Count, sessionId);
            }
        }

        // Resolve the working memory namespace for this context
        var wmNamespace = workingMemoryNamespace ?? $"session/{sessionId}";
        var isUserSession = wmNamespace.StartsWith("session/", StringComparison.OrdinalIgnoreCase);

        // Working memory inventory — own namespace
        var workingEntries = await workingMemory.ListAsync(wmNamespace);
        if (workingEntries.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var lines = workingEntries.Select(e =>
            {
                var remaining = e.ExpiresAt - now;
                var remainingStr = remaining.TotalMinutes >= 1
                    ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                    : $"{Math.Max(0, remaining.Seconds)}s";
                var meta = new System.Text.StringBuilder($"- {e.Key}: expires in {remainingStr}");
                if (e.Category is not null) meta.Append($", category: {e.Category}");
                if (e.Tags is { Count: > 0 }) meta.Append($", tags: {string.Join(", ", e.Tags)}");
                return meta.ToString();
            });
            var workingMemoryContext =
                "Working memory (scratch space — use search_working_memory or get_from_working_memory to retrieve):\n" +
                string.Join("\n", lines);
            chatMessages.Add(new ChatMessage(ChatRole.System, workingMemoryContext));
            logger.LogInformation("Injected {Count} working memory entries into context", workingEntries.Count);
        }

        // For user sessions: also surface any patrol findings so the primary agent is
        // automatically aware of what patrol tasks have stored since the last session.
        if (isUserSession)
        {
            var patrolEntries = await workingMemory.ListAsync("patrol");
            if (patrolEntries.Count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                var lines = patrolEntries.Select(e =>
                {
                    var remaining = e.ExpiresAt - now;
                    var remainingStr = remaining.TotalMinutes >= 1
                        ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                        : $"{Math.Max(0, remaining.Seconds)}s";
                    var meta = new System.Text.StringBuilder($"- {e.Key}: expires in {remainingStr}");
                    if (e.Category is not null) meta.Append($", category: {e.Category}");
                    if (e.Tags is { Count: > 0 }) meta.Append($", tags: {string.Join(", ", e.Tags)}");
                    return meta.ToString();
                });
                var patrolContext =
                    "Patrol findings in working memory (keys listed below):\n" +
                    string.Join("\n", lines) + "\n\n" +
                    "- To read a finding: call get_from_working_memory with the full key.\n" +
                    "- To dismiss a resolved finding: call delete_from_working_memory with the full key. " +
                    "Do this when the user confirms something is resolved or not a real issue — dismissed entries stop being re-surfaced.\n" +
                    "- To change what the patrol checks and reports: edit the 'patrol/proactive-actions' skill via save_skill. " +
                    "The patrol runs on a schedule and loads that skill as its directive each run.";
                chatMessages.Add(new ChatMessage(ChatRole.System, patrolContext));
                logger.LogInformation("Injected {Count} patrol working memory entries into context", patrolEntries.Count);
            }
        }

        // For user sessions: surface subagent research index chunks so the primary agent
        // can access prior research results even after the subagent completion turn has scrolled
        // out of the conversation window. To avoid noise, we inject at most two:
        //   1. The most recent index (always — likely what the user is following up on)
        //   2. One BM25-matched index (if a different one is more relevant to the current question)
        if (isUserSession)
        {
            var subagentEntries = await workingMemory.ListAsync("subagent");
            if (subagentEntries.Count > 0)
            {
                var indexEntries = subagentEntries.Where(e => e.Key.EndsWith("-index")).ToList();
                var nonIndexCount = subagentEntries.Count - indexEntries.Count;

                if (indexEntries.Count > 0)
                {
                    // Always include the most recent index
                    var mostRecent = indexEntries.OrderByDescending(e => e.StoredAt).First();
                    var selected = new List<WorkingMemoryEntry> { mostRecent };

                    // BM25-rank the rest against the current user message to find one more relevant match
                    if (indexEntries.Count > 1 && !string.IsNullOrWhiteSpace(currentUserContent))
                    {
                        var candidates = indexEntries.Where(e => e.Key != mostRecent.Key).ToList();

                        // Load content for BM25 ranking
                        var contentMap = new Dictionary<string, string>();
                        foreach (var entry in candidates)
                        {
                            var content = await workingMemory.GetAsync(entry.Key);
                            if (!string.IsNullOrWhiteSpace(content))
                                contentMap[entry.Key] = content;
                        }

                        if (contentMap.Count > 0)
                        {
                            var ranked = Bm25Ranker.RankWithScores(
                                candidates.Where(e => contentMap.ContainsKey(e.Key)).ToList(),
                                e => contentMap[e.Key],
                                currentUserContent);

                            if (ranked.Count > 0)
                                selected.Add(ranked[0].Item);
                        }
                    }

                    var now = DateTimeOffset.UtcNow;
                    var lines = selected.Distinct().Select(e =>
                    {
                        var remaining = e.ExpiresAt - now;
                        var remainingStr = remaining.TotalMinutes >= 1
                            ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                            : $"{Math.Max(0, remaining.Seconds)}s";
                        var meta = new System.Text.StringBuilder($"- {e.Key}: expires in {remainingStr}");
                        if (e.Category is not null) meta.Append($", category: {e.Category}");
                        if (e.Tags is { Count: > 0 }) meta.Append($", tags: {string.Join(", ", e.Tags)}");
                        return meta.ToString();
                    });
                    var otherCount = indexEntries.Count - selected.Distinct().Count();
                    var otherNote = otherCount > 0
                        ? $"\n({otherCount} other subagent index(es) available — use search_working_memory to find them)"
                        : string.Empty;
                    var subagentContext =
                        $"Subagent research in working memory ({nonIndexCount} content chunk(s) available):\n" +
                        "The following document outlines are from prior subagent research. " +
                        "Retrieve an outline with get_from_working_memory to see section headings and chunk keys, " +
                        "then load specific chunks as needed. Check these BEFORE doing a new web search for the same topic.\n" +
                        string.Join("\n", lines) + otherNote;
                    chatMessages.Add(new ChatMessage(ChatRole.System, subagentContext));
                    logger.LogInformation("Injected {Count} subagent index entries into context (of {Total} total, {ContentCount} content chunks available)",
                        selected.Distinct().Count(), indexEntries.Count, nonIndexCount);
                }
            }
        }

        Activity.Current?.AddEvent(new ActivityEvent("context_built",
            tags: new ActivityTagsCollection
            {
                { "message_count", chatMessages.Count },
                { "estimated_tokens", chatMessages.Sum(m => (m.Text?.Length ?? 0) / 4 + 1) }
            }));

        return chatMessages;
    }
}
