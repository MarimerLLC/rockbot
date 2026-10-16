using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Canonical implementation of the head+tail+stash context-window trim, shared by the
/// native tool-calling path (<see cref="RockBotFunctionInvokingChatClient"/>) and the
/// text-based loop (<see cref="AgentLoopRunner"/>). Both paths previously carried
/// byte-identical copies of this loop; the copies drifted only in field names, and a
/// non-termination bug had to be diagnosed and fixed in two places. It now lives here
/// once.
/// </summary>
internal static class ToolResultTrimmer
{
    private const int CharsPerToken = 4;

    /// <summary>
    /// Smallest surface a result is ever cut to, and only to get back under the hard budget.
    /// </summary>
    private const int MinFloorChars = 200;

    /// <summary>Default <see cref="AgentHostOptions.ToolResultTrimFloorChars"/>.</summary>
    public const int DefaultFloorChars = 1_000;

    private const string LegacySuffix = "\n[truncated to fit context window]";

    /// <summary>
    /// Brings the estimated context size back under <paramref name="maxTokens"/> by replacing
    /// tool results with a head+tail surface, stashing each full original in working memory
    /// under <c>stash/{sessionId}/{callId}</c>.
    ///
    /// <para><b>Cache stability (#710).</b> Every trim rewrites history the provider has
    /// already cached, so the next call reads nothing past the trimmed message. The trim
    /// therefore fires rarely and does a lot when it does: nothing happens until the context
    /// exceeds the budget, and then results are cut, oldest first, until it is down to
    /// <paramref name="targetRatio"/> of the budget. The iterations that follow append onto
    /// an unchanged prefix until the budget is crossed again. Oldest-first keeps the rewrite
    /// to the earliest point in the loop that can give up space, and leaves recent results,
    /// which the model is most likely still working from, intact.</para>
    ///
    /// <para><b>Protected results.</b> The most recent batch of tool results and results
    /// from self-paging tools (<see cref="SelfPagingTools"/>, issue #664) are cut only when
    /// the hard budget can't be met any other way, and then only by as much as it takes.</para>
    ///
    /// <para><b>Working-memory retrievals (#713).</b> Explicit retrievals
    /// (<see cref="StashExemptTools"/>) are never re-stashed: that mints a fresh key and loops
    /// the model re-fetching its own growing reference. An older <c>get_from_working_memory</c>
    /// result is still cut like any other, but its marker sends the model back to its own call
    /// and the key it passed there, so re-reading returns the same content and the stash
    /// registry gains nothing.
    /// A retrieval in the latest batch is never cut, nor is one whose key is namespace-relative
    /// (the trim can't resolve it) or has expired (it couldn't be re-read).
    /// <c>search_working_memory</c> results are never cut.</para>
    ///
    /// <para><b>Termination.</b> Each pass visits every candidate at most once, so the trim
    /// always ends; a context whose non-tool content alone exceeds the budget logs a warning
    /// instead of spinning (the 2026-06-05 incident: 400k+ identical "Trimmed tool result"
    /// log lines, CPU pegged at ~2 cores, until the pod was killed).</para>
    /// </summary>
    public static async Task TrimAsync(
        List<ChatMessage> messages,
        int maxTokens,
        string? sessionId,
        AgentLoopStashContext.State? stashState,
        IWorkingMemory workingMemory,
        double headTailRatio,
        int stashTtlMinutes,
        ILogger logger,
        ImageCostOptions? imageCost = null,
        double targetRatio = 1.0,
        int floorChars = DefaultFloorChars)
    {
        var charBudget = (int)(maxTokens * CharsPerToken * 0.9);
        var totalChars = messages.Sum(m => AgentLoopRunner.EstimateMessageChars(m, imageCost, logger));
        if (totalChars <= charBudget)
            return;

        var targetBudget = (int)(charBudget * Math.Clamp(targetRatio, 0.05, 1.0));
        var floor = Math.Max(MinFloorChars, floorChars);
        var headRatio = Math.Clamp(headTailRatio, 0.0, 1.0);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, stashTtlMinutes));
        var startChars = totalChars;
        var trimmedCount = 0;

        var candidates = await CollectCandidatesAsync(messages, workingMemory, logger);
        var ordinary = candidates.Where(c => c.Kind == CandidateKind.Ordinary).ToList();
        var latest = candidates.Where(c => c.Kind == CandidateKind.LatestBatch).ToList();
        var selfPaging = candidates.Where(c => c.Kind == CandidateKind.SelfPaging).ToList();

        // Down to the low-water mark with older results — to the floor first, then below it, so
        // floor-sized results piling up over a long loop can't pin the context near the budget
        // and bring back a trim every iteration — then the hard budget at any cost.
        (List<Candidate> Set, int Goal, int Floor)[] passes =
        [
            (ordinary, targetBudget, floor),
            (ordinary, targetBudget, MinFloorChars),
            (latest, charBudget, MinFloorChars),
            (selfPaging, charBudget, MinFloorChars),
        ];

        for (var p = 0; p < passes.Length; p++)
        {
            var (set, goal, passFloor) = passes[p];
            if (totalChars <= goal)
                continue;

            // Past the first pass, cutting is only worth it when it gets the context under budget.
            // When content the trim can't touch (system prompt, tool schemas, working-memory
            // retrievals) keeps it over budget regardless, the trim fires again on the next call
            // anyway, and cutting old results further, or the result the model just asked for,
            // saves little and loses the cache from the earliest rewritten message.
            if (p > 0 && totalChars - Reclaimable(messages, set, passFloor) > charBudget)
                continue;

            foreach (var candidate in set)
            {
                if (totalChars <= goal)
                    break;

                var saved = await TrimOneAsync(
                    messages, candidate, desiredReduction: totalChars - goal, passFloor,
                    sessionId, stashState, workingMemory, headRatio, ttl, logger);
                if (saved > 0)
                {
                    totalChars -= saved;
                    trimmedCount++;
                }
            }
        }

        if (trimmedCount > 0)
        {
            logger.LogInformation(
                "Tool-result trim: {Before:N0} → {After:N0} chars across {Count} result(s) " +
                "(budget {Budget:N0}, target {Target:N0})",
                startChars, totalChars, trimmedCount, charBudget, targetBudget);
        }

        if (totalChars > charBudget)
        {
            // The trim runs before every tool call; once per run is enough to say it can't keep up.
            var level = stashState is null || !stashState.BudgetUnreachableWarned ? LogLevel.Warning : LogLevel.Debug;
            if (stashState is not null)
                stashState.BudgetUnreachableWarned = true;
            logger.Log(level,
                "Tool-result trim cannot reach budget: total {Total:N0} > budget {Budget:N0} chars; the rest is " +
                "content the trim doesn't cut (system prompt, tool schemas, recent or unresolvable working-memory " +
                "retrievals) or results it won't cut further. Context may still exceed the limit.",
                totalChars, charBudget);
        }
    }

    /// <summary>
    /// The most a pass could save by cutting every result in <paramref name="set"/> to <paramref name="floor"/>,
    /// or, for a retrieval, to its marker when that is longer than the floor (see <see cref="TrimRetrieval"/>).
    /// </summary>
    private static int Reclaimable(List<ChatMessage> messages, List<Candidate> set, int floor) =>
        set.Sum(c =>
        {
            var frc = messages[c.MessageIndex].Contents[c.ContentIndex] as FunctionResultContent;
            var len = frc?.Result?.ToString()?.Length ?? 0;
            var min = c.RetrievalKey is not null && frc is not null
                ? Math.Max(floor, AgentLoopRunner.BuildRetrievalElisionMarker(frc.CallId).Length)
                : floor;
            return Math.Max(0, len - min);
        });

    private enum CandidateKind { Ordinary, LatestBatch, SelfPaging }

    /// <param name="RetrievalKey">
    /// For a <c>get_from_working_memory</c> result, the key it read. The result is cut with a
    /// marker pointing back at that key rather than stashed (#713).
    /// </param>
    private sealed record Candidate(int MessageIndex, int ContentIndex, CandidateKind Kind, string? RetrievalKey = null);

    /// <summary>
    /// Trimmable tool results in message order (oldest first), classified. The latest batch is
    /// the tool results after the assistant message that issued the most recent answered calls:
    /// in the text loop that is the tail of the list, and inside the function-invoking loop's
    /// pre-invoke hook it is the batch before the assistant turn now being executed.
    /// </summary>
    private static async Task<List<Candidate>> CollectCandidatesAsync(
        List<ChatMessage> messages, IWorkingMemory workingMemory, ILogger logger)
    {
        var lastTool = messages.FindLastIndex(m => m.Role == ChatRole.Tool);
        var latestBatchStart = lastTool < 0
            ? int.MaxValue
            : messages.FindLastIndex(lastTool, m => m.Role == ChatRole.Assistant) + 1;

        var candidates = new List<Candidate>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != ChatRole.Tool) continue;
            for (var j = 0; j < messages[i].Contents.Count; j++)
            {
                if (messages[i].Contents[j] is not FunctionResultContent frc) continue;
                var toolName = ExtractToolNameForCallId(messages, frc.CallId);
                // Never re-stash an explicit working-memory retrieval — re-stashing mints a
                // fresh key on every pass and loops the model forever re-fetching its own
                // growing reference. See StashExemptTools. An older retrieval can still be cut,
                // with a marker pointing back at the key it read (#713).
                if (StashExemptTools.Contains(toolName))
                {
                    if (i >= latestBatchStart)
                        continue;
                    var key = await ResolveRetrievalKeyAsync(messages, frc.CallId, toolName, workingMemory, logger);
                    if (key is not null)
                        candidates.Add(new Candidate(i, j, CandidateKind.Ordinary, key));
                    continue;
                }

                var kind = SelfPagingTools.Contains(toolName) ? CandidateKind.SelfPaging
                    : i >= latestBatchStart ? CandidateKind.LatestBatch
                    : CandidateKind.Ordinary;
                candidates.Add(new Candidate(i, j, kind));
            }
        }
        return candidates;
    }

    /// <summary>
    /// The key a <c>get_from_working_memory</c> call read, when the trim can safely cut its result:
    /// the key is a full path (a plain key is relative to a namespace the trim doesn't know) and
    /// the entry still exists, so the model can re-read it. Null otherwise, and for any other tool.
    /// </summary>
    private static async Task<string?> ResolveRetrievalKeyAsync(
        List<ChatMessage> messages, string callId, string toolName, IWorkingMemory workingMemory, ILogger logger)
    {
        if (!StashExemptTools.IsKeyedRetrieval(toolName))
            return null;

        var call = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .FirstOrDefault(c => string.Equals(c.CallId, callId, StringComparison.Ordinal));
        if (call?.Arguments is null || !call.Arguments.TryGetValue("key", out var raw))
            return null;

        var key = raw switch
        {
            string str => str,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('/'))
            return null;

        try
        {
            return await workingMemory.GetAsync(key) is null ? null : key;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check working-memory entry {Key}; leaving its retrieval intact", key);
            return null;
        }
    }

    /// <summary>
    /// Cuts one tool result by up to <paramref name="desiredReduction"/> chars, never below
    /// <paramref name="floor"/>. Returns the chars saved, or 0 when the result is already at the
    /// floor and rewriting it would not shrink it.
    /// </summary>
    private static async Task<int> TrimOneAsync(
        List<ChatMessage> messages,
        Candidate candidate,
        int desiredReduction,
        int floor,
        string? sessionId,
        AgentLoopStashContext.State? stashState,
        IWorkingMemory workingMemory,
        double headRatio,
        TimeSpan ttl,
        ILogger logger)
    {
        var old = (FunctionResultContent)messages[candidate.MessageIndex].Contents[candidate.ContentIndex];
        var oldStr = old.Result?.ToString() ?? string.Empty;
        var desiredLen = Math.Max(floor, oldStr.Length - desiredReduction);
        if (desiredLen >= oldStr.Length)
            return 0;

        if (candidate.RetrievalKey is { } retrievalKey)
            return TrimRetrieval(messages, candidate, oldStr, desiredLen, retrievalKey, headRatio, logger);

        // No callId or no stash state → legacy head-only truncation. The model has no way to
        // retrieve elided content (nothing registered), so the elision marker would only mislead.
        if (stashState is null || string.IsNullOrEmpty(old.CallId))
        {
            var keep = desiredLen - LegacySuffix.Length;
            if (keep <= 0)
                return 0;
            var legacyTrimmed = oldStr[..keep] + LegacySuffix;
            messages[candidate.MessageIndex].Contents[candidate.ContentIndex] =
                new FunctionResultContent(old.CallId, legacyTrimmed);
            logger.LogInformation(
                "Trimmed tool result (no callId, legacy mode): {Before:N0} → {After:N0} chars",
                oldStr.Length, legacyTrimmed.Length);
            return oldStr.Length - legacyTrimmed.Length;
        }

        var marker = AgentLoopRunner.BuildElisionMarker(old.CallId);
        // Total surface = head + "\n\n" + marker + "\n\n" + tail.
        var surfaceBudget = desiredLen - marker.Length - 4;
        if (surfaceBudget <= 0)
            return 0;
        var headLen = (int)Math.Round(surfaceBudget * headRatio);
        var tailLen = surfaceBudget - headLen;

        var head = headLen > 0 ? oldStr[..headLen] : string.Empty;
        var tail = tailLen > 0 ? oldStr[^tailLen..] : string.Empty;
        var trimmed = string.Concat(head, "\n\n", marker, "\n\n", tail);
        if (trimmed.Length >= oldStr.Length)
            return 0;

        // First trim of this callId: stash the full original and register the entry so the
        // next stash-registry update surfaces its key.
        if (!stashState.Registry.Contains(old.CallId))
        {
            var stashKey = AgentLoopRunner.BuildStashKey(sessionId, old.CallId);
            try
            {
                await workingMemory.SetAsync(
                    stashKey, oldStr, ttl,
                    category: "tool-result-stash",
                    tags: ["stash", "tool-result"]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to stash original tool result for call {CallId}; trimming without stash",
                    old.CallId);
            }

            stashState.ArgsSummaries.TryGetValue(old.CallId, out var argsSummary);
            stashState.Registry.Add(new ToolResultStashRegistry.Entry(
                CallId: old.CallId,
                ToolName: ExtractToolNameForCallId(messages, old.CallId),
                ArgsSummary: argsSummary ?? "(args unavailable)",
                Key: stashKey));
        }

        messages[candidate.MessageIndex].Contents[candidate.ContentIndex] =
            new FunctionResultContent(old.CallId, trimmed);

        logger.LogInformation(
            "Trimmed tool result for call {CallId}: {Before:N0} → {After:N0} chars (head {Head}, tail {Tail})",
            old.CallId, oldStr.Length, trimmed.Length, headLen, tailLen);
        return oldStr.Length - trimmed.Length;
    }

    /// <summary>
    /// Cuts a working-memory retrieval to a head+tail surface whose marker sends the model back to
    /// its own earlier call. The marker carries no key: keys inside tool output are untrusted, and
    /// the key the model needs is in its own call's arguments. Nothing is stashed and nothing is
    /// registered: repeating the call returns the same content, so there is no fresh key for the
    /// model to chase (#713, the 2026-06-10 loop).
    /// </summary>
    private static int TrimRetrieval(
        List<ChatMessage> messages,
        Candidate candidate,
        string oldStr,
        int desiredLen,
        string key,
        double headRatio,
        ILogger logger)
    {
        var old = (FunctionResultContent)messages[candidate.MessageIndex].Contents[candidate.ContentIndex];
        var marker = AgentLoopRunner.BuildRetrievalElisionMarker(old.CallId);
        // Total surface = head + "\n\n" + marker + "\n\n" + tail. Below that, the marker alone:
        // the floor can be smaller than the marker, and the marker is all the model needs.
        var surfaceBudget = desiredLen - marker.Length - 4;
        string trimmed;
        if (surfaceBudget <= 0)
        {
            trimmed = marker;
        }
        else
        {
            var headLen = (int)Math.Round(surfaceBudget * headRatio);
            var tailLen = surfaceBudget - headLen;
            var head = headLen > 0 ? oldStr[..headLen] : string.Empty;
            var tail = tailLen > 0 ? oldStr[^tailLen..] : string.Empty;
            trimmed = string.Concat(head, "\n\n", marker, "\n\n", tail);
        }
        if (trimmed.Length >= oldStr.Length)
            return 0;

        messages[candidate.MessageIndex].Contents[candidate.ContentIndex] =
            new FunctionResultContent(old.CallId, trimmed);

        logger.LogInformation(
            "Trimmed working-memory retrieval of {Key} for call {CallId}: {Before:N0} → {After:N0} chars",
            key, old.CallId, oldStr.Length, trimmed.Length);
        return oldStr.Length - trimmed.Length;
    }

    /// <summary>
    /// Walks <paramref name="messages"/> for the assistant <see cref="FunctionCallContent"/>
    /// whose CallId matches <paramref name="callId"/> and returns its tool name. Falls back
    /// to <c>"(unknown)"</c> when no match is found.
    /// </summary>
    private static string ExtractToolNameForCallId(List<ChatMessage> messages, string callId)
    {
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc &&
                    string.Equals(fcc.CallId, callId, StringComparison.Ordinal))
                {
                    return fcc.Name;
                }
            }
        }
        return "(unknown)";
    }
}
