using Microsoft.Extensions.Logging;
using RockBot.Observation;

namespace RockBot.Host;

/// <summary>
/// Host-side adapter that reads the existing <see cref="IConversationLog"/>
/// and produces <see cref="TranscriptTurn"/> records for the observation
/// framework. Source values are derived from session-id prefixes so the
/// framework's filters can scope what each target sees.
/// </summary>
/// <remarks>
/// <para>
/// The conversation log is naturally the "since last dream" window because
/// <c>RunPreferenceInferencePassAsync</c> calls <c>ClearAsync</c> at the end
/// of every dream cycle. The observation phase MUST run before preference
/// inference so it sees the same data.
/// </para>
/// <para>
/// Session prefixes mapped to <see cref="TranscriptSources"/>:
/// </para>
/// <list type="bullet">
///   <item><c>patrol/...</c> — scheduled tasks and heartbeat patrols</item>
///   <item><c>a2a-inbound/...</c> — calls from other agents (mapped to <see cref="TranscriptSources.Agent"/> since the human user is not the originator)</item>
///   <item>everything else — treated as a normal user conversation</item>
/// </list>
/// <para>
/// Within a normal user conversation, the role determines the source for
/// each turn: <c>role=user</c> → <see cref="TranscriptSources.User"/>;
/// anything else (assistant, system) → <see cref="TranscriptSources.Agent"/>.
/// </para>
/// </remarks>
public sealed class ConversationLogTranscriptAdapter(
    IConversationLog conversationLog,
    ILogger<ConversationLogTranscriptAdapter> logger)
{
    /// <summary>
    /// Reads the current conversation log and returns the entries as
    /// <see cref="TranscriptTurn"/> records sorted by timestamp. Each turn
    /// is given a stable <c>turnId</c> derived from its position within the
    /// session so the observation framework can quote-validate against it.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptTurn>> GetTranscriptAsync(
        CancellationToken cancellationToken)
    {
        var entries = await conversationLog.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            logger.LogDebug("Observation adapter: conversation log is empty");
            return [];
        }

        // Stable per-session ordering: for each session, ordered turns get
        // an index. The turnId combines session and index so the framework
        // can resolve a quote back to a specific entry.
        var bySession = entries
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var result = new List<TranscriptTurn>(entries.Count);
        foreach (var group in bySession)
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            var source = MapSource(group.Key);
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                var turnSource = source ?? MapRoleToSource(entry.Role);
                result.Add(new TranscriptTurn(
                    ConversationId: entry.SessionId,
                    TurnId: $"t{i}",
                    Source: turnSource,
                    Role: entry.Role,
                    Content: entry.Content,
                    Timestamp: entry.Timestamp));
            }
        }

        logger.LogDebug(
            "Observation adapter: produced {TurnCount} turns across {SessionCount} sessions",
            result.Count, bySession.Count());

        return result;
    }

    /// <summary>
    /// When the session prefix uniquely identifies a non-user origin (patrol,
    /// inbound A2A), returns that source. Otherwise returns <c>null</c> so
    /// per-turn role mapping decides between <c>User</c> and <c>Agent</c>.
    /// </summary>
    private static string? MapSource(string sessionId)
    {
        if (sessionId.StartsWith("patrol/", StringComparison.OrdinalIgnoreCase))
            return TranscriptSources.ScheduledTask;
        if (sessionId.StartsWith("a2a-inbound/", StringComparison.OrdinalIgnoreCase))
            return TranscriptSources.Agent;
        return null;
    }

    private static string MapRoleToSource(string role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
            ? TranscriptSources.User
            : TranscriptSources.Agent;
}
