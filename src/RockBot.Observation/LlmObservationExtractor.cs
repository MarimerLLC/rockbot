using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationExtractor"/> backed by <see cref="ILlmClient"/>.
/// Sends the target's extraction prompt + a formatted transcript and parses a
/// JSON-shaped response into <see cref="ProposedObservation"/> records.
/// Routine LLM failures (timeouts, malformed JSON, gateway saturation) are
/// caught and logged; the method returns an empty list so the surrounding
/// pipeline can skip the conversation and continue.
/// </summary>
internal sealed class LlmObservationExtractor(
    ILlmClient llmClient,
    ILogger<LlmObservationExtractor> logger) : IObservationExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<ProposedObservation>> ExtractAsync(
        ObservationTarget target,
        IReadOnlyList<TranscriptTurn> conversationTurns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(conversationTurns);

        if (conversationTurns.Count == 0)
            return [];

        var conversationId = conversationTurns[0].ConversationId;

        var userMessage = BuildUserMessage(target, conversationTurns);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, target.ExtractionPrompt),
            new(ChatRole.User, userMessage),
        };

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        ChatResponse response;
        try
        {
            response = await llmClient.GetResponseAsync(
                messages, target.ExtractionTier, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Observation: extraction LLM call failed for target {Target} conversation {Conversation}; skipping",
                target.Name, conversationId);
            return [];
        }

        var raw = response.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            logger.LogDebug(
                "Observation: extraction returned no text for target {Target} conversation {Conversation}",
                target.Name, conversationId);
            return [];
        }

        var json = ExtractJson(raw);
        if (json is null)
        {
            logger.LogWarning(
                "Observation: could not extract JSON from extraction response for target {Target} conversation {Conversation}",
                target.Name, conversationId);
            return [];
        }

        ExtractionResponseDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ExtractionResponseDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Observation: malformed JSON in extraction response for target {Target} conversation {Conversation}",
                target.Name, conversationId);
            return [];
        }

        if (parsed?.Observations is null || parsed.Observations.Count == 0)
            return [];

        var proposals = new List<ProposedObservation>(parsed.Observations.Count);
        foreach (var obs in parsed.Observations)
        {
            if (string.IsNullOrWhiteSpace(obs.Text)) continue;
            if (string.IsNullOrWhiteSpace(obs.Quote)) continue;
            if (string.IsNullOrWhiteSpace(obs.ConversationId)) continue;
            if (string.IsNullOrWhiteSpace(obs.TurnId)) continue;
            proposals.Add(new ProposedObservation(
                obs.Text.Trim(),
                obs.ConversationId.Trim(),
                obs.TurnId.Trim(),
                obs.Quote.Trim()));
        }

        return proposals;
    }

    private static string BuildUserMessage(
        ObservationTarget target,
        IReadOnlyList<TranscriptTurn> turns)
    {
        var sb = new StringBuilder();
        sb.Append("Conversation ID: ").AppendLine(turns[0].ConversationId);
        sb.AppendLine();
        sb.AppendLine("Turns:");
        foreach (var t in turns)
        {
            sb.Append("[turnId=").Append(t.TurnId)
              .Append(" role=").Append(t.Role)
              .Append(" source=").Append(t.Source)
              .Append(" at=").Append(t.Timestamp.ToString("u"))
              .AppendLine("]");
            sb.AppendLine(t.Content);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object of the shape:");
        sb.AppendLine("""
        {
          "observations": [
            { "text": "...", "conversationId": "...", "turnId": "...", "quote": "..." }
          ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Each observation MUST cite a specific turnId and a verbatim quote from that turn that supports the claim. Observations whose quote is not present in the cited turn will be discarded.");

        return sb.ToString();
    }

    /// <summary>
    /// Pulls the first balanced JSON object out of a free-form LLM response.
    /// Some models prepend or append narration even when JSON mode is requested.
    /// Returns null if no balanced object is found.
    /// </summary>
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
                if (depth == 0)
                    return raw.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private sealed class ExtractionResponseDto
    {
        public List<ObservationDto>? Observations { get; set; }
    }

    private sealed class ObservationDto
    {
        public string? Text { get; set; }
        public string? ConversationId { get; set; }
        public string? TurnId { get; set; }
        public string? Quote { get; set; }
    }
}
