using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationEvaluator"/> backed by <see cref="ILlmClient"/>.
/// Uses differential framing: candidates are verdicted against the existing
/// theories as a fixed reference, which is much harder to confabulate than
/// open-ended generation.
/// </summary>
internal sealed class LlmObservationEvaluator(
    ILlmClient llmClient,
    ILogger<LlmObservationEvaluator> logger) : IObservationEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<EvaluationVerdict>> EvaluateAsync(
        ObservationTarget target,
        IReadOnlyList<Candidate> eligibleCandidates,
        IReadOnlyList<Theory> existingTheories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(eligibleCandidates);
        ArgumentNullException.ThrowIfNull(existingTheories);

        if (eligibleCandidates.Count == 0)
            return [];

        var userMessage = BuildUserMessage(eligibleCandidates, existingTheories);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, target.EvaluationPrompt),
            new(ChatRole.User, userMessage),
        };

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        ChatResponse response;
        try
        {
            response = await llmClient.GetResponseAsync(
                messages, target.EvaluationTier, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Observation: evaluation LLM call failed for target {Target}; promotion is skipped this dream",
                target.Name);
            return [];
        }

        var raw = response.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return [];

        var json = ExtractJson(raw);
        if (json is null)
        {
            logger.LogWarning(
                "Observation: could not extract JSON from evaluation response for target {Target}",
                target.Name);
            return [];
        }

        EvaluationResponseDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EvaluationResponseDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Observation: malformed JSON in evaluation response for target {Target}",
                target.Name);
            return [];
        }

        if (parsed?.Verdicts is null) return [];

        var results = new List<EvaluationVerdict>(parsed.Verdicts.Count);
        foreach (var v in parsed.Verdicts)
        {
            if (string.IsNullOrWhiteSpace(v.CandidateId))
                continue;

            var action = v.Action?.Trim().ToLowerInvariant() switch
            {
                "promote" => EvaluationAction.Promote,
                "refine" => EvaluationAction.Refine,
                "reject" => EvaluationAction.Reject,
                _ => EvaluationAction.Unspecified,
            };

            results.Add(new EvaluationVerdict(
                v.CandidateId.Trim(),
                action,
                string.IsNullOrWhiteSpace(v.RefinedText) ? null : v.RefinedText.Trim(),
                string.IsNullOrWhiteSpace(v.Reason) ? null : v.Reason.Trim()));
        }

        return results;
    }

    private static string BuildUserMessage(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Theory> theories)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Existing theories:");
        if (theories.Count == 0)
        {
            sb.AppendLine("(none yet)");
        }
        else
        {
            foreach (var t in theories)
                sb.Append("- ").Append(t.Id).Append(": ").AppendLine(t.Text);
        }
        sb.AppendLine();

        sb.AppendLine("Candidates eligible for promotion (have crossed the reinforcement threshold):");
        foreach (var c in candidates)
        {
            sb.Append("- id=").Append(c.Id)
              .Append(" reinforced=").Append(c.Count).Append(" distinct conversations")
              .AppendLine();
            sb.Append("  text: ").AppendLine(c.Text);
            sb.AppendLine("  representative quotes:");
            foreach (var r in c.References.TakeLast(3))
            {
                sb.Append("    - [").Append(r.ConversationId).Append('/').Append(r.TurnId).Append("] \"")
                  .Append(Truncate(r.Quote, 200)).AppendLine("\"");
            }
        }
        sb.AppendLine();

        sb.AppendLine("For each candidate, choose action: promote / refine / reject.");
        sb.AppendLine("- promote: candidate is well-grounded by its quotes, distinct from existing theories, ready to graduate");
        sb.AppendLine("- refine: candidate captures something real but its text should be reworded; provide refinedText");
        sb.AppendLine("- reject: candidate is not grounded, conflicts with an existing theory, or is too noisy");
        sb.AppendLine();
        sb.AppendLine("""
        Respond JSON of shape:
        {
          "verdicts": [
            { "candidateId": "...", "action": "promote|refine|reject", "refinedText": "...", "reason": "..." }
          ]
        }
        """);

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return raw.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private sealed class EvaluationResponseDto
    {
        public List<VerdictDto>? Verdicts { get; set; }
    }

    private sealed class VerdictDto
    {
        public string? CandidateId { get; set; }
        public string? Action { get; set; }
        public string? RefinedText { get; set; }
        public string? Reason { get; set; }
    }
}
