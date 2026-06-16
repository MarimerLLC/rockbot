using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A;

/// <summary>
/// Safety net for A2A replies that arrive after the subagent that issued the invocation has
/// already exited. Such replies are not user-facing on the subagent's own session, so the
/// receive-side handlers normally stash them to working memory and return — but if the
/// subagent is gone, nobody consumes that entry and the reply is silently lost.
///
/// This folder recovers the subagent's owning primary (user) session via
/// <see cref="ISubagentSessionResolver"/>, stashes the payload under the primary's
/// <c>notifications/</c> namespace, and publishes a <see cref="LateA2ANotificationMessage"/>
/// so <see cref="LateA2ANotificationHandler"/> can surface it in a fresh primary turn.
/// </summary>
internal sealed class A2ALateReplyFolder(
    IMessagePublisher publisher,
    IWorkingMemory workingMemory,
    AgentIdentity agent,
    A2AOptions options,
    ILogger<A2ALateReplyFolder> logger,
    ISubagentSessionResolver? resolver = null)
{
    /// <summary>
    /// Attempts to fold a late reply back to the originating subagent's primary session.
    /// No-ops (returning false) when the origin is not a recoverable subagent session or the
    /// subagent is still active — in those cases the caller's existing behavior (return /
    /// let the awaiter deliver) is correct.
    /// </summary>
    public async Task<bool> TryFoldBackAsync(
        PendingA2ATask pending, string a2aTaskId, NotificationKind kind, string payloadText, CancellationToken ct)
    {
        var origin = pending.PrimarySessionId;

        if (resolver is null || !resolver.IsSubagentSession(origin))
            return false;

        // Still running: the existing a2aAwaiter path (subagent blocks on outstanding A2A
        // before publishing) handles delivery. Don't double-surface.
        if (resolver.IsActive(origin))
            return false;

        var primary = resolver.ResolvePrimarySession(origin);
        if (primary is null || !IsUserSession(primary))
        {
            logger.LogWarning(
                "Late A2A {Kind} for task {TaskId} from subagent session {Origin} cannot be folded back " +
                "(primary unresolved or non-user: {Primary}) — dropping", kind, a2aTaskId, origin, primary);
            return false;
        }

        var subagentTaskId = ExtractSubagentTaskId(origin);
        var kindSlug = kind.ToString().ToLowerInvariant();
        var wmKey = $"{primary}/notifications/a2a/{subagentTaskId}/{kindSlug}";

        await workingMemory.SetAsync(
            wmKey, payloadText,
            ttl: TimeSpan.FromMinutes(60),
            category: "a2a-late-notification",
            tags: [pending.TargetAgent, a2aTaskId, kindSlug]);

        // Append to the primary's notifications index so list_working_memory surfaces it
        // and the directive-driven per-turn check can find pending notifications.
        var indexKey = $"{primary}/notifications/index";
        var existing = await workingMemory.GetAsync(indexKey) ?? string.Empty;
        var line = $"{kindSlug} from '{pending.TargetAgent}' (subagent {subagentTaskId}) -> {wmKey}";
        await workingMemory.SetAsync(
            indexKey, existing.Length == 0 ? line + "\n" : existing + line + "\n",
            ttl: TimeSpan.FromMinutes(60),
            category: "notifications-index");

        var message = new LateA2ANotificationMessage(
            PrimarySessionId: primary,
            SubagentTaskId: subagentTaskId,
            SubagentName: $"subagent-{subagentTaskId}",
            PeerAgent: pending.TargetAgent,
            Kind: kind,
            WorkingMemoryKey: wmKey);

        var envelope = message.ToEnvelope<LateA2ANotificationMessage>(source: agent.Name);
        await publisher.PublishAsync($"{options.LateNotificationTopic}.{agent.Name}", envelope, ct);

        logger.LogInformation(
            "Folded late A2A {Kind} for task {TaskId} from terminated subagent {SubagentTaskId} back to " +
            "primary session {Primary} (wm key {Key})", kind, a2aTaskId, subagentTaskId, primary, wmKey);
        return true;
    }

    // Mirrors the IsUserSession guard used by the A2A receive-side handlers.
    private static bool IsUserSession(string sessionId) =>
        sessionId.StartsWith("session/", StringComparison.OrdinalIgnoreCase) &&
        !sessionId.StartsWith("session/subagent-", StringComparison.OrdinalIgnoreCase);

    private static string ExtractSubagentTaskId(string origin)
    {
        foreach (var prefix in new[] { "subagent/", "session/subagent-", "subagent-" })
        {
            if (origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return origin[prefix.Length..];
        }
        return origin;
    }
}
