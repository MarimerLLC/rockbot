using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// For each persona, runs a second IChatClient call that revises the persona's view in
/// light of sibling views and names explicit tensions. Per-persona parallel.
/// </summary>
internal sealed class CritiqueStep(
    IChatClient chatClient,
    ILogger<CritiqueStep> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed record CritiqueOutput(PersonaView RevisedView, IReadOnlyList<Tension> Tensions);

    public async Task<CritiqueOutput> RunAsync(
        Persona persona,
        string question,
        PersonaView ownView,
        IReadOnlyList<PersonaView> siblingViews,
        CancellationToken ct)
    {
        if (siblingViews.Count == 0)
            return new CritiqueOutput(ownView, []);

        var system = persona.SystemPrompt + "\n\n## Critique addendum\n" +
            "Revise your view in light of the sibling views below. Be explicit when you disagree — " +
            "name what you disagree with and why. Identify tensions between your framing and others'. " +
            "Stay in your persona's framing; do not adopt another persona's view wholesale. " +
            "Respond ONLY with JSON of the form: " +
            "{ \"revised_view\": \"markdown prose\", \"key_points\": [\"...\"], \"tensions\": " +
            "[{\"with\":\"sibling_id\",\"description\":\"...\",\"stakes\":\"...\"}] }";

        var user = BuildUserPrompt(question, ownView, siblingViews);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        };
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, ct);
            var raw = response.Text ?? string.Empty;
            var parsed = Parse(raw, persona.Id);
            if (parsed is not null)
                return parsed;
            logger.LogWarning("CritiqueStep parse failed for persona {Id}; keeping original view", persona.Id);
            return new CritiqueOutput(ownView, []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CritiqueStep failed for persona {Id}", persona.Id);
            return new CritiqueOutput(ownView, []);
        }
    }

    private static string BuildUserPrompt(string question, PersonaView ownView, IReadOnlyList<PersonaView> siblings)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question).AppendLine();
        sb.AppendLine("Your earlier view:");
        sb.AppendLine(ownView.View).AppendLine();
        sb.AppendLine("Sibling persona views:");
        foreach (var s in siblings)
        {
            sb.Append("### ").AppendLine(s.Id);
            sb.AppendLine(s.View).AppendLine();
        }
        return sb.ToString();
    }

    private static CritiqueOutput? Parse(string raw, string ownPersonaId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("revised_view", out var rvEl)) return null;
            var revised = rvEl.GetString();
            if (string.IsNullOrWhiteSpace(revised)) return null;

            var keyPoints = new List<string>();
            if (root.TryGetProperty("key_points", out var kpEl) && kpEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var kp in kpEl.EnumerateArray())
                {
                    var s = kp.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) keyPoints.Add(s);
                }
            }

            var tensions = new List<Tension>();
            if (root.TryGetProperty("tensions", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tEl.EnumerateArray())
                {
                    var with = item.TryGetProperty("with", out var wEl) ? wEl.GetString() : null;
                    var desc = item.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? string.Empty : string.Empty;
                    var stakes = item.TryGetProperty("stakes", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrWhiteSpace(with) && !string.IsNullOrWhiteSpace(desc))
                        tensions.Add(new Tension([ownPersonaId, with!], desc, stakes));
                }
            }

            var revisedView = new PersonaView(ownPersonaId, revised!, keyPoints, []);
            return new CritiqueOutput(revisedView, tensions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
