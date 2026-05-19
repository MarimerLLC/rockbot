using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Final integration step. High-tier IChatClient call: takes persona views and any
/// identified tensions, returns structured CouncilResponse JSON.
/// </summary>
internal sealed class SynthesizeStep(
    IChatClient chatClient,
    ILogger<SynthesizeStep> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed record SynthesisInput(
        string Question,
        IReadOnlyList<PersonaView> Views,
        IReadOnlyList<Tension> Tensions);

    public sealed record SynthesisOutput(string Synthesis, string Confidence, IReadOnlyList<Tension> Tensions);

    public async Task<SynthesisOutput> RunAsync(SynthesisInput input, CancellationToken ct)
    {
        var system = BuildSystemPrompt();
        var user = BuildUserPrompt(input);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        };
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        SynthesisOutput? result = null;
        var attempt = 1;
        string? raw = null;
        while (attempt <= 2 && result is null)
        {
            try
            {
                var response = await chatClient.GetResponseAsync(messages, options, ct);
                raw = response.Text ?? string.Empty;
                result = ParseAndValidate(raw, input.Tensions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SynthesizeStep attempt {Attempt} failed", attempt);
            }

            if (result is null)
            {
                attempt++;
                messages.Add(new ChatMessage(ChatRole.User,
                    "Your previous response was not valid JSON matching the schema. " +
                    "Return ONLY a JSON object: { \"synthesis\": \"...\", \"confidence\": \"low|medium|high\", " +
                    "\"tensions\": [{\"between\":[\"a\",\"b\"],\"description\":\"...\",\"stakes\":\"...\"}] }."));
            }
        }

        if (result is null)
        {
            logger.LogWarning("SynthesizeStep falling back to minimal synthesis; raw={Raw}", Truncate(raw, 400));
            return new SynthesisOutput(
                "Synthesis was unavailable. Persona views are included in the structured response.",
                "low",
                input.Tensions);
        }
        return result;
    }

    private static string BuildSystemPrompt() =>
        """
        You are the synthesis step of an advisor council. Several personas have examined
        a question from distinct framings. Integrate their views into a single piece of
        guidance that names the tensions, surfaces what is at stake, and reaches an
        integrated recommendation where possible.

        Guidelines:
        - Do not just summarize. Integrate. Name tensions explicitly.
        - Be honest about uncertainty. Use confidence: "low" when personas disagree
          substantively, "medium" when there is partial alignment, "high" when consensus.
        - Your synthesis prose should be ~500 words, well-structured, plain markdown.

        Respond with JSON only:
        { "synthesis": "markdown prose", "confidence": "low|medium|high", "tensions": [{"between":["id_a","id_b"],"description":"...","stakes":"..."}] }
        """;

    private static string BuildUserPrompt(SynthesisInput input)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(input.Question).AppendLine();

        var contributing = input.Views.Where(v => v.Status == PersonaStatus.Ok).ToList();
        var missing = input.Views.Where(v => v.Status != PersonaStatus.Ok).ToList();

        if (missing.Count > 0)
        {
            sb.AppendLine("Coverage note: the following personas were selected but did NOT contribute and are absent from the views below. Acknowledge this gap in your synthesis (briefly) and lower the confidence accordingly.");
            foreach (var v in missing)
                sb.Append("- ").Append(v.Id).Append(" (").Append(v.Status).AppendLine(")");
            sb.AppendLine();
        }

        sb.AppendLine("Persona views:");
        foreach (var v in contributing)
        {
            sb.Append("### ").AppendLine(v.Id);
            sb.AppendLine(v.View).AppendLine();
        }
        if (input.Tensions.Count > 0)
        {
            sb.AppendLine("Tensions already identified by personas during critique:");
            foreach (var t in input.Tensions)
            {
                sb.Append("- between [").Append(string.Join(", ", t.Between)).Append("]: ")
                  .Append(t.Description).Append(" (stakes: ").Append(t.Stakes).AppendLine(")");
            }
        }
        return sb.ToString();
    }

    private static SynthesisOutput? ParseAndValidate(string raw, IReadOnlyList<Tension> fallbackTensions)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJson(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("synthesis", out var synEl)) return null;
            var synthesis = synEl.GetString();
            if (string.IsNullOrWhiteSpace(synthesis)) return null;
            var confidence = root.TryGetProperty("confidence", out var confEl)
                ? confEl.GetString() ?? "medium"
                : "medium";
            confidence = confidence.ToLowerInvariant() switch
            {
                "low" or "medium" or "high" => confidence.ToLowerInvariant(),
                _ => "medium"
            };

            var tensions = fallbackTensions;
            if (root.TryGetProperty("tensions", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Tension>();
                foreach (var item in tEl.EnumerateArray())
                {
                    var between = new List<string>();
                    if (item.TryGetProperty("between", out var bEl) && bEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in bEl.EnumerateArray())
                        {
                            var bs = b.GetString();
                            if (!string.IsNullOrWhiteSpace(bs)) between.Add(bs);
                        }
                    }
                    var desc = item.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? string.Empty : string.Empty;
                    var stakes = item.TryGetProperty("stakes", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty;
                    if (between.Count > 0 && !string.IsNullOrWhiteSpace(desc))
                        list.Add(new Tension(between, desc, stakes));
                }
                if (list.Count > 0)
                    tensions = list;
            }

            return new SynthesisOutput(synthesis!, confidence, tensions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty :
        s.Length <= max ? s : s[..max] + "…";
}
