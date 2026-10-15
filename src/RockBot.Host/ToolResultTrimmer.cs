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
    /// the hard budget can't be met any other way, and then only by as much as it takes.
    /// Explicit working-memory retrievals (<see cref="StashExemptTools"/>) are never cut.</para>
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

        var candidates = CollectCandidates(messages);
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

        foreach (var (set, goal, passFloor) in passes)
        {
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
            logger.LogWarning(
                "Tool-result trim cannot reach budget: total {Total:N0} > budget {Budget:N0} chars with every " +
                "trimmable tool result at the elision floor. Context may still exceed the limit.",
                totalChars, charBudget);
        }
    }

    private enum CandidateKind { Ordinary, LatestBatch, SelfPaging }

    private sealed record Candidate(int MessageIndex, int ContentIndex, CandidateKind Kind);

    /// <summary>
    /// Trimmable tool results in message order (oldest first), classified. The latest batch is
    /// the tool results after the assistant message that issued the most recent answered calls:
    /// in the text loop that is the tail of the list, and inside the function-invoking loop's
    /// pre-invoke hook it is the batch before the assistant turn now being executed.
    /// </summary>
    private static List<Candidate> CollectCandidates(List<ChatMessage> messages)
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
                // growing reference. See StashExemptTools.
                if (StashExemptTools.Contains(toolName)) continue;

                var kind = SelfPagingTools.Contains(toolName) ? CandidateKind.SelfPaging
                    : i >= latestBatchStart ? CandidateKind.LatestBatch
                    : CandidateKind.Ordinary;
                candidates.Add(new Candidate(i, j, kind));
            }
        }
        return candidates;
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
