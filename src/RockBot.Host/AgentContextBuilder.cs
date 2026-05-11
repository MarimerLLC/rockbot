using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IEnumerable<IKnowledgeGraph> knowledgeGraphProviders,
    IOptions<KnowledgeGraphOptions> knowledgeGraphOptions,
    IEnumerable<IEmbeddingGenerator<string, Embedding<float>>> embeddingGenerators,
    ILogger<AgentContextBuilder> logger,
    ICapabilityClaimVerifier? capabilityClaimVerifier = null,
    IToolCallLog? toolCallLog = null)
{
    private const int MaxLlmContextTurns = 20;
    private readonly IServiceSearchIndex? _serviceSearchIndex = serviceSearchIndexProviders.FirstOrDefault();
    private readonly IKnowledgeGraph? _knowledgeGraph = knowledgeGraphProviders.FirstOrDefault();
    private readonly KnowledgeGraphOptions _graphOptions = knowledgeGraphOptions.Value;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator = embeddingGenerators.FirstOrDefault();

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
        // Derive a category for prompt-hint injection (Phase 4 PromptBuilderHint).
        // Categories are the top-level segment of the working-memory namespace —
        // "session", "patrol", "subagent" — so a hint file like
        // /data/agent/prompt-hints/patrol.md is injected into patrol-task prompts only.
        var category = DerivePromptCategory(workingMemoryNamespace);
        var systemPrompt = systemPromptOverride ?? promptBuilder.Build(profile, agent, category);
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

        // Short low-signal messages ("ok", "I'll find out soon", "any idea why?")
        // produce noisy BM25 hits that drown out the recent conversation thread.
        // When the message is below the threshold, skip per-turn topic searches
        // (long-term memory, episodic, skill recall, service hints) and the
        // embedding generation that backs them. Conversation history, rules,
        // identity, and working memory still flow — those are session grounding,
        // not topic-search. See issue #383.
        var isShortMessage = !string.IsNullOrWhiteSpace(currentUserContent)
            && currentUserContent.Length <= ShortMessageHeuristics.UserMessageCharThreshold;
        if (isShortMessage)
            logger.LogInformation(
                "Short user message ({Len} chars ≤ {Threshold}) — skipping per-turn topic search injection",
                currentUserContent.Length, ShortMessageHeuristics.UserMessageCharThreshold);

        // ── Wave 0: generate the query embedding once, shared across all searches ──
        // Avoids redundant calls to the embedding endpoint — each store would otherwise
        // generate its own query embedding for the same user message text. Skipped
        // for short messages since none of the gated searches will run.

        float[]? sharedQueryEmbedding = null;
        if (_embeddingGenerator is not null && !string.IsNullOrWhiteSpace(currentUserContent) && !isShortMessage)
        {
            try
            {
                var result = await _embeddingGenerator.GenerateAsync(currentUserContent, cancellationToken: ct);
                sharedQueryEmbedding = result.Vector.ToArray();
                logger.LogInformation("Shared query embedding generated ({Dimensions} dimensions)", sharedQueryEmbedding.Length);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate shared query embedding — stores will fall back to BM25-only");
            }
        }

        // ── Wave 1: fire all independent lookups concurrently ─────────────────
        // Each store is a separate singleton with its own locking, so cross-store
        // parallelism reduces wall-clock time from sum(all) to max(slowest store).
        // The pre-computed query embedding is passed to all searches so they skip
        // generating their own.

        var wmNamespace = workingMemoryNamespace ?? $"session/{sessionId}";
        var isUserSession = wmNamespace.StartsWith("session/", StringComparison.OrdinalIgnoreCase);

        // Evaluate skill index gate synchronously (has side effects — marks session).
        var shouldInjectSkillIndex = skillIndexTracker.TryMarkAsInjected(sessionId);

        var historyTask = conversationMemory.GetTurnsAsync(sessionId, ct);
        var ltmTask = isShortMessage
            ? Task.FromResult<IReadOnlyList<MemoryEntry>>([])
            : longTermMemory.SearchAsync(
                new MemorySearchCriteria(Query: currentUserContent, MaxResults: 8, QueryEmbedding: sharedQueryEmbedding));
        var episodicTask = isShortMessage
            ? Task.FromResult<IReadOnlyList<MemoryEntry>>([])
            : longTermMemory.SearchAsync(
                new MemorySearchCriteria(Query: currentUserContent, Category: "episodic", MaxResults: 5, QueryEmbedding: sharedQueryEmbedding));
        var identityTask = longTermMemory.SearchAsync(
            new MemorySearchCriteria(Category: AgentIdentityCategories.Prefix, MaxResults: 20));
        var skillListTask = shouldInjectSkillIndex
            ? skillStore.ListAsync()
            : Task.FromResult<IReadOnlyList<Skill>>([]);
        var skillSearchTask = isShortMessage
            ? Task.FromResult<IReadOnlyList<Skill>>([])
            : skillStore.SearchAsync(currentUserContent, maxResults: 5, ct, queryEmbedding: sharedQueryEmbedding);
        var wmTask = workingMemory.ListAsync(wmNamespace);
        var graphTask = _knowledgeGraph?.FindEntitiesByNameAsync(currentUserContent);
        var patrolTask = isUserSession
            ? workingMemory.ListAsync("patrol")
            : Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        var subagentTask = isUserSession
            ? workingMemory.ListAsync("subagent")
            : Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        // shared/ is the conventional cross-session handoff namespace — visible to every
        // context (user, patrol, subagent) so entries written by one session are discoverable
        // by any other without prior knowledge of the writer's namespace.
        var sharedTask = workingMemory.ListAsync("shared");

        // Await all wave-1 tasks. graphTask may be null when no knowledge graph is configured.
        if (graphTask is not null)
            await Task.WhenAll(historyTask, ltmTask, episodicTask, identityTask,
                skillListTask, skillSearchTask, wmTask, graphTask, patrolTask, subagentTask, sharedTask);
        else
            await Task.WhenAll(historyTask, ltmTask, episodicTask, identityTask,
                skillListTask, skillSearchTask, wmTask, patrolTask, subagentTask, sharedTask);

        var history = historyTask.Result;
        var recalled = ltmTask.Result;
        var episodes = episodicTask.Result;
        var identityEntries = identityTask.Result;
        var skillList = skillListTask.Result;
        var recalledSkills = skillSearchTask.Result;
        var workingEntries = wmTask.Result;
        var matchedEntities = graphTask?.Result;
        var patrolEntries = patrolTask.Result;
        var subagentEntries = subagentTask.Result;
        var sharedEntries = sharedTask.Result;

        // Amendment 1 step 4: evict capability-claim-shaped observations contradicted
        // by a more-recent successful call to the same (server, tool). Without this,
        // false rationalisations like "the wrapper cannot pass arguments" linger as
        // observations until TTL and keep re-injecting through the WM listings.
        workingEntries = await FilterStaleObservationsAsync(workingEntries, ct);
        patrolEntries = await FilterStaleObservationsAsync(patrolEntries, ct);
        sharedEntries = await FilterStaleObservationsAsync(sharedEntries, ct);
        subagentEntries = await FilterStaleObservationsAsync(subagentEntries, ct);

        // ── Wave 2: conditional lookups that depend on wave 1 results ─────────

        // Long-term memory fallback when first search returned nothing on first turn.
        if (recalled.Count == 0 && history.Count == 1)
            recalled = await longTermMemory.SearchAsync(new MemorySearchCriteria(MaxResults: 5));

        // Phase 2 read-side falsification: capability-claim entries (entries with a
        // VerifyShape under category claim/capability/*) get re-verified before injection.
        // Predicate-succeeded claims are evicted from the store and dropped from injection;
        // uncertain claims are kept but annotated so the LLM sees the unsettled status.
        var uncertainClaimAnnotations = new Dictionary<string, string>(StringComparer.Ordinal);
        recalled = await FilterCapabilityClaimsAsync(recalled, uncertainClaimAnnotations, ct);
        episodes = await FilterCapabilityClaimsAsync(episodes, uncertainClaimAnnotations, ct);
        identityEntries = await FilterCapabilityClaimsAsync(identityEntries, uncertainClaimAnnotations, ct);

        // Knowledge graph traverse: BFS from user-seed entities at MaxHops, then expand
        // a second time from top-ranked recalled memories at the (shorter) MemorySeedMaxHops.
        // User-seed triples fill the budget first; memory-seed triples fill any remaining
        // capacity and are deduped by triple ID. Admission for memory seeds is ranked-K
        // (scores are not exposed by ILongTermMemory) — K=2 is conservative so BM25-only
        // deployments do not amplify keyword-collision noise.
        IReadOnlyList<KnowledgeTriple>? graphTriples = null;
        var userSeedTripleCount = 0;
        var memorySeedTripleCount = 0;
        var mutualTripleCount = 0;
        List<(string MemoryId, IReadOnlyList<string> SeedEntityIds, IReadOnlyList<string> TripleIds)>? memorySeedSources = null;

        if (_knowledgeGraph is not null)
        {
            var userSeedIds = matchedEntities is { Count: > 0 }
                ? matchedEntities.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<KnowledgeTriple> userTriples = [];
            if (userSeedIds.Count > 0)
            {
                var userSeedList = userSeedIds.ToList();
                _ = _knowledgeGraph.TouchEntitiesAsync(userSeedList);
                userTriples = await _knowledgeGraph.TraverseAsync(userSeedList, _graphOptions.MaxHops, ct);
            }
            userSeedTripleCount = userTriples.Count;
            var userTripleIds = userTriples.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var memoryTripleMap = new Dictionary<string, KnowledgeTriple>(StringComparer.OrdinalIgnoreCase);
            var mutualTripleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_graphOptions.MaxMemorySeedSources > 0 && recalled.Count > 0)
            {
                memorySeedSources = [];
                var topMemories = recalled.Take(_graphOptions.MaxMemorySeedSources);

                foreach (var entry in topMemories)
                {
                    if (string.IsNullOrWhiteSpace(entry.Content))
                    {
                        memorySeedSources.Add((entry.Id, [], []));
                        continue;
                    }

                    var memEntities = await _knowledgeGraph.FindEntitiesByNameAsync(entry.Content, ct);
                    var memSeedIds = memEntities
                        .Select(e => e.Id)
                        .Where(id => !userSeedIds.Contains(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (memSeedIds.Count == 0)
                    {
                        memorySeedSources.Add((entry.Id, [], []));
                        continue;
                    }

                    _ = _knowledgeGraph.TouchEntitiesAsync(memSeedIds);
                    var memTriples = await _knowledgeGraph.TraverseAsync(
                        memSeedIds, _graphOptions.MemorySeedMaxHops, ct);

                    var perMemTripleIds = new List<string>(memTriples.Count);
                    foreach (var t in memTriples)
                    {
                        perMemTripleIds.Add(t.Id);
                        if (userTripleIds.Contains(t.Id))
                            mutualTripleIds.Add(t.Id);
                        else
                            memoryTripleMap.TryAdd(t.Id, t);
                    }

                    memorySeedSources.Add((entry.Id, memSeedIds, perMemTripleIds));
                }
            }

            memorySeedTripleCount = memoryTripleMap.Count;
            mutualTripleCount = mutualTripleIds.Count;

            if (userTriples.Count > 0 || memoryTripleMap.Count > 0)
            {
                var combined = new List<KnowledgeTriple>(userTriples.Count + memoryTripleMap.Count);
                combined.AddRange(userTriples);
                combined.AddRange(memoryTripleMap.Values);
                graphTriples = combined;
            }
        }

        // ── Assemble chatMessages in deterministic order ──────────────────────

        // Recent conversation history
        var startIndex = Math.Max(0, history.Count - MaxLlmContextTurns);
        for (var i = startIndex; i < history.Count; i++)
        {
            var turn = history[i];
            var role = turn.Role == "user" ? ChatRole.User : ChatRole.Assistant;
            chatMessages.Add(new ChatMessage(role, turn.Content));
        }

        // Long-term memory BM25 recall
        {
            var newEntries = recalled
                .Where(e => injectedMemoryTracker.TryMarkAsInjected(sessionId, e.Id))
                .ToList();

            if (newEntries.Count > 0)
            {
                var lines = newEntries.Select(e =>
                    $"- [{e.Id}] ({e.Category ?? "general"}, importance={e.ImportanceScore:F2}): {e.Content}{ClaimAnnotation(uncertainClaimAnnotations, e.Id)}");
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

        // Episodic memory recall
        {
            var newEpisodes = episodes
                .Where(e => injectedMemoryTracker.TryMarkAsInjected(sessionId, e.Id))
                .ToList();

            if (newEpisodes.Count > 0)
            {
                var lines = newEpisodes.Select(e =>
                    $"- [{e.Id}] (importance={e.ImportanceScore:F2}): {e.Content}{ClaimAnnotation(uncertainClaimAnnotations, e.Id)}");
                var episodicContext =
                    "Relevant past experiences (episodic memory):\n" +
                    string.Join("\n", lines);
                chatMessages.Add(new ChatMessage(ChatRole.System, episodicContext));
                logger.LogInformation(
                    "Injected {Count} episodic memory entries for session {SessionId}",
                    newEpisodes.Count, sessionId);
            }
        }

        // Narrative identity injection
        {
            if (identityEntries.Count > 0)
            {
                var isPrimary = systemPromptOverride is null;
                var lines = identityEntries.Select(e =>
                    $"- ({e.Category ?? AgentIdentityCategories.SelfModel}): {e.Content}{ClaimAnnotation(uncertainClaimAnnotations, e.Id)}");

                string identityContext;
                if (isPrimary)
                {
                    identityContext =
                        "Your evolving identity (complements your core soul — these reflect how your understanding " +
                        "of your role has developed through experience):\n" +
                        string.Join("\n", lines);
                }
                else
                {
                    identityContext =
                        "Primary agent identity context (you are a subordinate agent supporting the primary agent — " +
                        "do not assume its role, speak to users on its behalf, or act as the primary orchestrator. " +
                        "This context helps you understand the agent you serve):\n" +
                        string.Join("\n", lines);
                }

                chatMessages.Add(new ChatMessage(ChatRole.System, identityContext));
                logger.LogInformation(
                    "Injected {Count} identity entries for session {SessionId} (primary={IsPrimary})",
                    identityEntries.Count, sessionId, isPrimary);
            }
        }

        // Knowledge graph expansion
        if (graphTriples is { Count: > 0 })
        {
            var newTriples = graphTriples
                .Take(_graphOptions.MaxExpandedTriples)
                .ToList();

            if (newTriples.Count > 0)
            {
                var lines = newTriples.Select(t =>
                    $"- {t.Subject} --{t.Predicate}--> {t.Object} (confidence={t.Confidence:F2})");
                var graphContext =
                    "Related knowledge graph connections:\n" +
                    string.Join("\n", lines);
                chatMessages.Add(new ChatMessage(ChatRole.System, graphContext));

                var userMatchCount = matchedEntities?.Count ?? 0;
                var memorySources = memorySeedSources is null
                    ? "[]"
                    : "[" + string.Join(", ", memorySeedSources.Select(s =>
                        $"{{MemoryId={s.MemoryId}, Seeds=[{string.Join(",", s.SeedEntityIds)}], Triples=[{string.Join(",", s.TripleIds)}]}}")) + "]";
                logger.LogInformation(
                    "Injected {Count} knowledge graph triples for {EntityCount} matched entities in session {SessionId} " +
                    "(UserSeedTripleCount={UserSeedTripleCount}, MemorySeedTripleCount={MemorySeedTripleCount}, " +
                    "MutualTripleCount={MutualTripleCount}, MemorySeedSources={MemorySeedSources})",
                    newTriples.Count, userMatchCount, sessionId,
                    userSeedTripleCount, memorySeedTripleCount, mutualTripleCount, memorySources);

                Activity.Current?.AddEvent(new ActivityEvent("kg_expansion_complete",
                    tags: new ActivityTagsCollection
                    {
                        { "injected_count", newTriples.Count },
                        { "user_seed_entity_count", userMatchCount },
                        { "memory_seed_source_count", memorySeedSources?.Count ?? 0 },
                        { "user_seed_triple_count", userSeedTripleCount },
                        { "memory_seed_triple_count", memorySeedTripleCount },
                        { "mutual_triple_count", mutualTripleCount }
                    }));
            }
        }

        // Skill index (once per session)
        if (shouldInjectSkillIndex && skillList.Count > 0)
        {
            var indexText =
                "Available skills (use get_skill to load full instructions; " +
                "bracketed tags like [Wisp, Python] list resource types saved on the skill):\n" +
                string.Join("\n", skillList.Select(s =>
                {
                    var summary = string.IsNullOrWhiteSpace(s.Summary)
                        ? "(summary pending)"
                        : s.Summary;
                    var resourceTag = SkillTools.FormatResourceTag(s.Manifest);
                    return $"- {s.Name}{resourceTag}: {summary}";
                }));
            chatMessages.Add(new ChatMessage(ChatRole.System, indexText));
            logger.LogInformation("Injected skill index ({Count} skills) for session {SessionId}",
                skillList.Count, sessionId);
        }

        // Per-turn skill BM25 recall
        {
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

        // Per-turn service search hints (A2A agents + MCP servers).
        // Gated on isShortMessage to match the BM25-search injections above —
        // a 2-3-word query produces noisy hits regardless of which index runs it.
        if (_serviceSearchIndex is not null && !string.IsNullOrWhiteSpace(currentUserContent) && !isShortMessage)
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

        // Working memory inventory — own namespace
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

        // Patrol findings
        if (isUserSession && patrolEntries.Count > 0)
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
                "- The patrol updates its own checklist each run based on what it learns. " +
                "If you want the patrol to check or report something differently, ask in this session " +
                "and the change can be made — patrol updates take effect on the next scheduled run.";
            chatMessages.Add(new ChatMessage(ChatRole.System, patrolContext));
            logger.LogInformation("Injected {Count} patrol working memory entries into context", patrolEntries.Count);
        }

        // Shared working memory — cross-session handoff namespace. Visible to every context
        // (user sessions, patrols, subagents). Skipped when the caller's own namespace is
        // already "shared" to avoid double-listing.
        if (sharedEntries.Count > 0 &&
            !wmNamespace.Equals("shared", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            var lines = sharedEntries.Select(e =>
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
            var sharedContext =
                "Shared working memory (cross-session handoff — any session, patrol, or subagent can write here):\n" +
                string.Join("\n", lines) + "\n\n" +
                "- Use get_from_working_memory with the full key (e.g. 'shared/drafts/tina-vslive') to read an entry.\n" +
                "- To write here, pass a full path key beginning with 'shared/' to save_to_working_memory " +
                "(e.g. 'shared/drafts/...', 'shared/pending/...'). Choose self-describing keys — other sessions " +
                "discover entries by name, not content.\n" +
                "- Prefer this over your own namespace when the entry is meant for another session to pick up.";
            chatMessages.Add(new ChatMessage(ChatRole.System, sharedContext));
            logger.LogInformation("Injected {Count} shared working memory entries into context", sharedEntries.Count);
        }

        // Subagent research index chunks
        if (isUserSession && subagentEntries.Count > 0)
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

        Activity.Current?.AddEvent(new ActivityEvent("context_built",
            tags: new ActivityTagsCollection
            {
                { "message_count", chatMessages.Count },
                { "estimated_tokens", chatMessages.Sum(m => (m.Text?.Length ?? 0) / 4 + 1) }
            }));

        return chatMessages;
    }

    /// <summary>
    /// Phase 2 read-side falsification gate. For each entry under
    /// <c>claim/capability/*</c> with a <see cref="VerifyShape"/>, runs the verifier
    /// and (a) drops + evicts entries whose predicate succeeds, (b) keeps entries
    /// whose predicate fails, (c) keeps entries whose verifier was uncertain and
    /// records an annotation in <paramref name="uncertainAnnotations"/>. Non-claim
    /// entries pass through unchanged. Returns the kept-for-injection list.
    /// </summary>
    private async Task<IReadOnlyList<MemoryEntry>> FilterCapabilityClaimsAsync(
        IReadOnlyList<MemoryEntry> entries,
        IDictionary<string, string> uncertainAnnotations,
        CancellationToken ct)
    {
        if (capabilityClaimVerifier is null || entries.Count == 0)
            return entries;

        var keep = new List<MemoryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Verify is null || !CapabilityClaimCategories.IsCapabilityClaim(entry.Category))
            {
                keep.Add(entry);
                continue;
            }

            VerifyResult result;
            try
            {
                result = await capabilityClaimVerifier.VerifyAsync(entry.Verify, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Capability claim verifier threw for entry {Id} ({Category}); injecting with uncertainty annotation",
                    entry.Id, entry.Category);
                uncertainAnnotations[entry.Id] = "verifier-error";
                keep.Add(entry);
                continue;
            }

            switch (result.Outcome)
            {
                case VerifyOutcome.PredicateSucceeded:
                    logger.LogInformation(
                        "Capability claim {Id} ({Category}) falsified by verifier — evicting from long-term memory",
                        entry.Id, entry.Category);
                    try
                    {
                        await longTermMemory.DeleteAsync(entry.Id, ct);
                    }
                    catch (Exception delEx)
                    {
                        logger.LogWarning(delEx, "Failed to evict falsified claim {Id}", entry.Id);
                    }
                    // Skip — do not inject.
                    break;

                case VerifyOutcome.PredicateFailed:
                    keep.Add(entry);
                    break;

                case VerifyOutcome.Uncertain:
                default:
                    uncertainAnnotations[entry.Id] = result.Detail ?? "uncertain";
                    keep.Add(entry);
                    break;
            }
        }

        return keep;
    }

    private static string ClaimAnnotation(IReadOnlyDictionary<string, string> uncertain, string id) =>
        uncertain.TryGetValue(id, out var detail)
            ? $" [verifier-uncertain: {detail}]"
            : string.Empty;

    /// <summary>
    /// Amendment 1 step 4: opportunistic falsification of working-memory entries
    /// tagged <see cref="ObservationLanguageDetector.ObservationTag"/>. For each
    /// entry whose content matches capability-claim language and names one or
    /// more <c>server/tool</c> references, looks for a successful tool call to
    /// the same pair in <see cref="IToolCallLog"/> that occurred AFTER the
    /// observation was written. A newer success contradicts the observation,
    /// so the entry is evicted from working memory and excluded from injection.
    ///
    /// "Successful" is determined from <see cref="ToolCallEvent.Succeeded"/>.
    /// The recency window is bounded by the earliest observation timestamp, so
    /// a single log query covers all observations under consideration.
    /// </summary>
    private async Task<IReadOnlyList<WorkingMemoryEntry>> FilterStaleObservationsAsync(
        IReadOnlyList<WorkingMemoryEntry> entries, CancellationToken ct)
    {
        if (toolCallLog is null || entries.Count == 0) return entries;

        // First pass: collect observation entries that could be falsified.
        var candidates = new List<(WorkingMemoryEntry Entry, IReadOnlyList<(string Server, string Tool)> Refs)>();
        foreach (var entry in entries)
        {
            if (!IsObservation(entry)) continue;
            if (!ObservationLanguageDetector.LooksLikeCapabilityClaim(entry.Value)) continue;
            var refs = ObservationLanguageDetector.TryExtractToolReferences(entry.Value);
            if (refs.Count == 0) continue;
            candidates.Add((entry, refs));
        }

        if (candidates.Count == 0) return entries;

        // Query the tool-call log once, since the earliest observation timestamp,
        // and let per-observation filtering narrow it down. Bound the result count
        // so a chatty session doesn't blow context.
        var earliest = candidates.Min(c => c.Entry.StoredAt);
        IReadOnlyList<ToolCallEvent> recentCalls;
        try
        {
            recentCalls = await toolCallLog.QueryRecentAsync(earliest, maxResults: 500, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "FilterStaleObservations: tool-call log query failed; skipping observation eviction");
            return entries;
        }

        var successes = recentCalls.Where(c => c.Succeeded).ToList();
        if (successes.Count == 0) return entries;

        // Eviction pass: keep everything that wasn't falsified.
        var toEvict = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, refs) in candidates)
        {
            var matched = successes.FirstOrDefault(s =>
                s.Timestamp > entry.StoredAt && CallMatchesAnyRef(s, refs));
            if (matched is null) continue;

            try
            {
                await workingMemory.DeleteAsync(entry.Key);
                toEvict.Add(entry.Key);
                logger.LogInformation(
                    "Evicted stale observation {Key} (stored {StoredAt:u}): contradicted by successful call {ToolName} (args={ArgsSummary}) at {SuccessAt:u}",
                    entry.Key, entry.StoredAt, matched.ToolName, matched.ArgumentsSummary ?? "(no args)",
                    matched.Timestamp);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "FilterStaleObservations: failed to evict {Key}; will pass through to injection",
                    entry.Key);
            }
        }

        return toEvict.Count == 0
            ? entries
            : entries.Where(e => !toEvict.Contains(e.Key)).ToList();
    }

    private static bool IsObservation(WorkingMemoryEntry entry) =>
        entry.Tags is not null
        && entry.Tags.Any(t => string.Equals(t, ObservationLanguageDetector.ObservationTag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A successful tool call matches a (server, tool) reference when both names
    /// appear in either the call's <see cref="ToolCallEvent.ToolName"/> or
    /// <see cref="ToolCallEvent.ArgumentsSummary"/>. MCP calls are logged as
    /// <c>mcp_invoke_tool</c> with the inner tool exposed via the args summary
    /// (<c>server_name=X, tool_name=Y</c>), so both fields must be searched.
    /// </summary>
    private static bool CallMatchesAnyRef(
        ToolCallEvent call, IReadOnlyList<(string Server, string Tool)> refs)
    {
        var hay = $"{call.ToolName} {call.ArgumentsSummary ?? string.Empty}";
        foreach (var r in refs)
        {
            if (hay.Contains(r.Server, StringComparison.OrdinalIgnoreCase)
                && hay.Contains(r.Tool, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the top-level segment of the working-memory namespace as the prompt
    /// category. Defaults to <c>"session"</c> when no namespace is supplied.
    /// </summary>
    internal static string? DerivePromptCategory(string? workingMemoryNamespace)
    {
        if (string.IsNullOrWhiteSpace(workingMemoryNamespace))
            return "session";

        var slash = workingMemoryNamespace.IndexOf('/');
        var head = slash < 0 ? workingMemoryNamespace : workingMemoryNamespace[..slash];
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }
}
