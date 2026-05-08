using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationExtractionPhase"/>. Drives extraction +
/// quote-grounding + vector clustering + merge for one target. Per-conversation
/// extraction calls run concurrently (bounded by the LLM gateway); the merge
/// step is in-memory and sequential.
/// </summary>
internal sealed class ObservationExtractionPhase(
    IObservationExtractor extractor,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IObservationStateStore stateStore,
    ILogger<ObservationExtractionPhase> logger) : IObservationExtractionPhase
{
    public async Task<ExtractionPhaseResult> ExecuteAsync(
        ObservationTarget target,
        IReadOnlyList<TranscriptTurn> transcripts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(transcripts);

        if (transcripts.Count == 0)
        {
            logger.LogInformation(
                "Observation: target {Target} has no transcripts in the dream window; phase 1 is a no-op",
                target.Name);
            return new ExtractionPhaseResult(0, 0, 0, 0, 0, 0, StateWritten: false);
        }

        // Group turns by conversation so each conversation gets its own
        // extraction call. Order within a conversation is preserved.
        var conversations = transcripts
            .GroupBy(t => t.ConversationId, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Turns: (IReadOnlyList<TranscriptTurn>)g.ToList()))
            .ToList();

        // Per-conversation extraction. Failures are caught inside the extractor
        // and surfaced as empty results; we count those separately.
        var perConversationResults = new List<(string ConvId, IReadOnlyList<TranscriptTurn> Turns, IReadOnlyList<ProposedObservation>? Proposals)>();
        var failed = 0;

        foreach (var (id, turns) in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var proposals = await extractor.ExtractAsync(target, turns, cancellationToken)
                    .ConfigureAwait(false);
                perConversationResults.Add((id, turns, proposals));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Observation: unhandled extractor exception for target {Target} conversation {Conversation}; skipping",
                    target.Name, id);
                failed++;
                perConversationResults.Add((id, turns, null));
            }
        }

        // If every conversation failed, exit without writing state per the
        // design's "whole batch failed" rule.
        if (failed == conversations.Count && conversations.Count > 0)
        {
            logger.LogWarning(
                "Observation: all {Count} conversations failed extraction for target {Target}; skipping state write",
                conversations.Count, target.Name);
            return new ExtractionPhaseResult(
                ConversationsProcessed: 0,
                ConversationsFailed: failed,
                ProposalsReceived: 0,
                ProposalsGrounded: 0,
                MatchedExistingCandidates: 0,
                NewCandidatesCreated: 0,
                StateWritten: false);
        }

        // Quote-grounding per conversation.
        var groundedAcrossBatch = new List<(ProposedObservation Proposal, DateTimeOffset TurnAt)>();
        var totalProposed = 0;
        foreach (var (_, turns, proposals) in perConversationResults)
        {
            if (proposals is null) continue;
            totalProposed += proposals.Count;

            var grounded = QuoteGrounding.Filter(proposals, turns).ToList();
            // For each grounded proposal, capture the source turn timestamp
            // so the resulting reference carries observation time.
            var turnsByid = turns.ToDictionary(t => t.TurnId, StringComparer.Ordinal);
            foreach (var g in grounded)
            {
                if (turnsByid.TryGetValue(g.TurnId, out var turn))
                    groundedAcrossBatch.Add((g, turn.Timestamp));
            }
        }

        var totalGrounded = groundedAcrossBatch.Count;

        if (totalGrounded == 0)
        {
            // Even if no grounded proposals, we still want to record that the
            // dream ran (LastDreamAt) so the next cycle's window is correct.
            // We keep the existing state otherwise untouched.
            var existing = await stateStore.LoadAsync(target, cancellationToken).ConfigureAwait(false);
            existing.LastDreamAt = DateTimeOffset.UtcNow;
            await stateStore.SaveAsync(target, existing, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Observation: target {Target} produced no grounded proposals from {Conversations} conversation(s) " +
                "(received {Proposed}, failed {Failed}); marking dream timestamp",
                target.Name, conversations.Count - failed, totalProposed, failed);

            return new ExtractionPhaseResult(
                ConversationsProcessed: conversations.Count - failed,
                ConversationsFailed: failed,
                ProposalsReceived: totalProposed,
                ProposalsGrounded: 0,
                MatchedExistingCandidates: 0,
                NewCandidatesCreated: 0,
                StateWritten: true);
        }

        // Embed grounded proposals.
        cancellationToken.ThrowIfCancellationRequested();

        var texts = groundedAcrossBatch.Select(g => g.Proposal.Text).ToList();
        var embeddingResults = await embeddings.GenerateAsync(texts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Load + mutate + save state (atomic write inside the store).
        var state = await stateStore.LoadAsync(target, cancellationToken).ConfigureAwait(false);

        var matched = 0;
        var created = 0;
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < groundedAcrossBatch.Count; i++)
        {
            var (proposal, turnAt) = groundedAcrossBatch[i];
            var embedding = embeddingResults[i].Vector.Span;

            var bestMatch = Clustering.FindBestMatch(
                embedding, state.Candidates, target.ClusteringSimilarityThreshold);

            var reference = new ObservationReference(
                proposal.ConversationId, proposal.TurnId, proposal.Quote, turnAt);

            if (bestMatch is { Candidate: var existing })
            {
                AppendReference(existing, reference, now);
                matched++;
            }
            else
            {
                state.Candidates.Add(CreateCandidate(proposal, reference, embedding.ToArray(), now));
                created++;
            }
        }

        state.LastDreamAt = now;
        await stateStore.SaveAsync(target, state, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Observation: target {Target} phase 1 complete — " +
            "{Conversations} conversation(s) processed ({Failed} failed), " +
            "{Proposed} proposed, {Grounded} grounded, " +
            "{Matched} matched existing, {Created} new candidates",
            target.Name,
            conversations.Count - failed, failed,
            totalProposed, totalGrounded,
            matched, created);

        return new ExtractionPhaseResult(
            ConversationsProcessed: conversations.Count - failed,
            ConversationsFailed: failed,
            ProposalsReceived: totalProposed,
            ProposalsGrounded: totalGrounded,
            MatchedExistingCandidates: matched,
            NewCandidatesCreated: created,
            StateWritten: true);
    }

    private static void AppendReference(Candidate existing, ObservationReference reference, DateTimeOffset now)
    {
        existing.References.Add(reference);
        existing.LastSeen = now;
        // Count = distinct conversations contributing.
        existing.Count = existing.References
            .Select(r => r.ConversationId)
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static Candidate CreateCandidate(
        ProposedObservation proposal,
        ObservationReference reference,
        float[] vector,
        DateTimeOffset now) =>
        new()
        {
            Id = "cand_" + Guid.NewGuid().ToString("N")[..12],
            Text = proposal.Text,
            ClusterId = "clust_" + Guid.NewGuid().ToString("N")[..12],
            Count = 1,
            FirstSeen = now,
            LastSeen = now,
            References = { reference },
            Vector = vector,
        };
}
