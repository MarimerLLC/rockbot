using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Copies the turns a <see cref="UserMessage"/> refers to (<see cref="UserMessage.RelatedReplies"/>)
/// into the message's own session, before it is answered (#841). The user saw those replies (a
/// subagent result, a scheduled task's output) in the client they are writing from, but the work
/// ran in another session, so without a copy "summarize that" reaches a session that never saw it.
/// </summary>
/// <remarks>
/// Each copy keeps the original's role, agent, source, attachments and origin, gets its own id and
/// the current time, and records where it came from in <see cref="ConversationTurn.ImportedFrom"/>.
/// <see cref="AgentContextBuilder"/> tells the model so; the stored content stays as the user saw it.
/// </remarks>
internal sealed class RelatedReplyImporter(
    IConversationMemory conversationMemory,
    SessionOriginStore originStore,
    ILogger<RelatedReplyImporter> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>At most this many references are imported for one message.</summary>
    internal const int MaxImports = 5;

    /// <summary>A copy's content is cut to this many characters.</summary>
    internal const int MaxContentChars = 8_000;

    internal const string TruncationNote = "\n\n…(truncated)";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sessionGates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Imports what <paramref name="message"/> refers to into its session. Skips a reference to the
    /// session itself, one already imported there, and one whose turn can't be found. Never throws
    /// for a reference it can't import.
    /// </summary>
    /// <returns>How many turns were imported.</returns>
    public async Task<int> ImportAsync(UserMessage message, CancellationToken ct = default)
    {
        if (message.RelatedReplies is not { Count: > 0 } references)
            return 0;

        var sessionId = message.SessionId;

        // One import at a time per session, so two messages in flight can't both copy a reply.
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ImportLockedAsync(sessionId, references, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> ImportLockedAsync(string sessionId, IReadOnlyList<ReplyReference> references, CancellationToken ct)
    {
        // Copies evicted past retention still count as imported.
        var existing = (await conversationMemory.GetTurnsAsync(sessionId, ct))
            .Concat(await conversationMemory.GetArchivedTurnsAsync(sessionId, ct));
        var imported = new HashSet<(string, string)>(
            existing.Where(t => t.ImportedFrom is not null).Select(t => (t.ImportedFrom!.SessionId, t.ImportedFrom.Id)));

        var count = 0;
        foreach (var reference in references.Take(MaxImports))
        {
            if (string.IsNullOrWhiteSpace(reference.SessionId) || string.IsNullOrWhiteSpace(reference.MessageId))
                continue;

            var fromSession = SessionIds.Raw(reference.SessionId);
            if (string.Equals(fromSession, sessionId, StringComparison.Ordinal))
                continue;
            if (!imported.Add((fromSession, reference.MessageId)))
                continue;

            try
            {
                var original = await FindAsync(fromSession, reference.MessageId, ct);
                if (original is null)
                {
                    logger.LogDebug("Related reply {MessageId} in session {FromSession} not found; not imported into {SessionId}",
                        reference.MessageId, fromSession, sessionId);
                    continue;
                }

                await conversationMemory.AddTurnAsync(sessionId, Copy(original, fromSession), ct);
                count++;
                logger.LogInformation("Imported related reply {MessageId} from session {FromSession} into {SessionId}",
                    reference.MessageId, fromSession, sessionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to import related reply {MessageId} from session {FromSession} into {SessionId}",
                    reference.MessageId, fromSession, sessionId);
            }
        }

        return count;
    }

    private async Task<ConversationTurn?> FindAsync(string sessionId, string id, CancellationToken ct)
    {
        var live = await conversationMemory.GetTurnsAsync(sessionId, ct);
        var turn = live.LastOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (turn is not null)
            return turn;

        var archived = await conversationMemory.GetArchivedTurnsAsync(sessionId, ct);
        return archived.LastOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
    }

    private ConversationTurn Copy(ConversationTurn original, string fromSession) =>
        original with
        {
            Id = ConversationTurn.NewId(),
            Content = Cap(original.Content),
            Timestamp = _time.GetUtcNow(),
            Origin = original.Origin ?? originStore.Get(fromSession).ToTurnOrigin(),
            ImportedFrom = new ConversationTurnReference(fromSession, original.Id!),
        };

    internal static string Cap(string content)
    {
        if (content.Length <= MaxContentChars)
            return content;
        // Don't leave half of a surrogate pair at the cut.
        var cut = char.IsHighSurrogate(content[MaxContentChars - 1]) ? MaxContentChars - 1 : MaxContentChars;
        return content[..cut].TrimEnd() + TruncationNote;
    }
}
