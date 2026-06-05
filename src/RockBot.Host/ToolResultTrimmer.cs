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
    /// Repeatedly replaces the largest tool result with a head+tail surface (stashing the
    /// full original in working memory under <c>stash/{sessionId}/{callId}</c>) until the
    /// estimated context size fits <paramref name="maxTokens"/>.
    ///
    /// <para><b>Termination.</b> The loop is bounded three ways, any one of which is
    /// sufficient: (1) it stops once the budget is met; (2) it stops when the largest
    /// remaining tool result cannot be shrunk (already at the elision floor, where
    /// re-adding the marker would keep it the same size or grow it) — since the largest
    /// can't shrink, nothing smaller can bring the total under budget either; (3) a hard
    /// pass cap as defence-in-depth. Without (2)/(3), a context whose <i>non-tool</i>
    /// content alone exceeds the budget (system prompt + injected skill bodies + the
    /// stash registry) re-trims the same floor-sized results forever — the 2026-06-05
    /// incident: 400k+ identical "Trimmed tool result" log lines, CPU pegged at ~2
    /// cores, until the pod was killed.</para>
    /// </summary>
    public static async Task TrimAsync(
        List<ChatMessage> messages,
        int maxTokens,
        string? sessionId,
        AgentLoopStashContext.State? stashState,
        IWorkingMemory workingMemory,
        double headTailRatio,
        int stashTtlMinutes,
        ILogger logger)
    {
        var charBudget = (int)(maxTokens * CharsPerToken * 0.9);
        var headRatio = Math.Clamp(headTailRatio, 0.0, 1.0);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, stashTtlMinutes));

        // Defence-in-depth pass cap. The no-progress breaks below are the real guarantee;
        // this catches any future edit that reintroduces a non-shrinking path.
        var maxPasses = messages.Count * 4 + 32;

        for (var pass = 0; ; pass++)
        {
            if (pass >= maxPasses)
            {
                logger.LogWarning(
                    "Tool-result trim exceeded {MaxPasses} passes without reaching the {Budget:N0}-char " +
                    "budget; stopping to avoid a non-terminating loop. Context may still exceed the limit.",
                    maxPasses, charBudget);
                break;
            }

            var totalChars = messages.Sum(AgentLoopRunner.EstimateMessageChars);
            if (totalChars <= charBudget)
                break;

            int bestMsg = -1, bestContent = -1, bestLen = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != ChatRole.Tool) continue;
                for (var j = 0; j < messages[i].Contents.Count; j++)
                {
                    if (messages[i].Contents[j] is FunctionResultContent frc)
                    {
                        var len = frc.Result?.ToString()?.Length ?? 0;
                        if (len > bestLen) { bestMsg = i; bestContent = j; bestLen = len; }
                    }
                }
            }

            if (bestMsg < 0)
                break;

            var old = (FunctionResultContent)messages[bestMsg].Contents[bestContent];
            var oldStr = old.Result?.ToString() ?? string.Empty;
            var excess = totalChars - charBudget;

            // No callId or no stash state → legacy head-only truncation. The model has no
            // way to retrieve elided content (nothing registered), so the elision marker
            // would only mislead.
            if (stashState is null || string.IsNullOrEmpty(old.CallId))
            {
                // Clamp the slice length so the 200-char floor can never index past the
                // end of a short result (which would throw).
                var legacyTarget = Math.Min(oldStr.Length, Math.Max(200, oldStr.Length - excess - 60));
                var legacyTrimmed = oldStr[..legacyTarget] + "\n[truncated to fit context window]";
                if (legacyTrimmed.Length >= oldStr.Length)
                {
                    logger.LogWarning(
                        "Tool-result trim (legacy) cannot reach budget: total {Total:N0} > budget {Budget:N0} " +
                        "chars, but the largest tool result ({Len:N0} chars) is already at the elision floor. " +
                        "Stopping to avoid a non-terminating loop; context may still exceed the limit.",
                        totalChars, charBudget, oldStr.Length);
                    break;
                }
                messages[bestMsg].Contents[bestContent] =
                    new FunctionResultContent(old.CallId, legacyTrimmed);
                logger.LogInformation(
                    "Trimmed tool result (no callId, legacy mode): {Before:N0} → {After:N0} chars",
                    bestLen, legacyTrimmed.Length);
                continue;
            }

            var marker = AgentLoopRunner.BuildElisionMarker(old.CallId);
            // Total surface = head + marker + tail. Budget the surface so the trimmed
            // message ends up shorter than the original by at least the excess.
            var surfaceBudget = Math.Max(200, oldStr.Length - excess - 60 - marker.Length);
            if (surfaceBudget >= oldStr.Length) surfaceBudget = oldStr.Length - 1;
            var headLen = (int)Math.Round(surfaceBudget * headRatio);
            var tailLen = surfaceBudget - headLen;
            if (headLen < 0) headLen = 0;
            if (tailLen < 0) tailLen = 0;
            if (headLen + tailLen >= oldStr.Length) headLen = Math.Max(0, oldStr.Length - tailLen - 1);

            var head = headLen > 0 ? oldStr[..headLen] : string.Empty;
            var tail = tailLen > 0 ? oldStr[^tailLen..] : string.Empty;
            var trimmed = string.Concat(head, "\n\n", marker, "\n\n", tail);

            // No-progress guard: the largest tool result is already at/under the elision
            // floor, so rewriting it (re-adding the marker) does not shrink it. Since we
            // always pick the largest result, nothing smaller can reach the budget either.
            if (trimmed.Length >= oldStr.Length)
            {
                logger.LogWarning(
                    "Tool-result trim cannot reach budget: total {Total:N0} > budget {Budget:N0} chars, " +
                    "but the largest tool result ({Len:N0} chars, call {CallId}) is already at the elision " +
                    "floor. Stopping to avoid a non-terminating loop; context may still exceed the limit.",
                    totalChars, charBudget, oldStr.Length, old.CallId);
                break;
            }

            // First trim of this callId: stash the full original and register the entry
            // so the next RefreshStashRegistryContext call surfaces it.
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

            messages[bestMsg].Contents[bestContent] = new FunctionResultContent(old.CallId, trimmed);

            logger.LogInformation(
                "Trimmed tool result for call {CallId}: {Before:N0} → {After:N0} chars (head {Head}, tail {Tail})",
                old.CallId, bestLen, trimmed.Length, headLen, tailLen);
        }
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
