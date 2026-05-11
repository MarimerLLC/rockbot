using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Picks which personas should examine the question and whether to run pre-research
/// and cross-critique. Single Balanced-tier IChatClient call.
/// </summary>
internal sealed class SelectStep(
    IChatClient chatClient,
    PersonaRegistry registry,
    ILogger<SelectStep> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<SelectorOutput> RunAsync(string question, CancellationToken ct)
    {
        var personas = registry.Personas;
        if (personas.Count == 0)
        {
            logger.LogWarning("No personas loaded — returning empty selection");
            return FallbackSelection(string.Empty);
        }

        var systemPrompt = BuildSystemPrompt(personas.Values);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, question)
        };
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        var attempt = 1;
        SelectorOutput? parsed = null;
        string? rawText = null;
        while (attempt <= 2 && parsed is null)
        {
            try
            {
                var response = await chatClient.GetResponseAsync(messages, options, ct);
                rawText = response.Text ?? string.Empty;
                parsed = ParseAndValidate(rawText, personas.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SelectStep attempt {Attempt} failed", attempt);
            }

            if (parsed is null)
            {
                attempt++;
                messages.Add(new ChatMessage(ChatRole.User,
                    "Your previous response was not valid JSON matching the required schema. " +
                    "Return ONLY a JSON object with keys: personas (array of {id, needs_research}), " +
                    "pre_research (boolean), critique (boolean), rationale (string)."));
            }
        }

        if (parsed is null)
        {
            logger.LogWarning("SelectStep falling back to default selection; raw={Raw}", Truncate(rawText, 400));
            return FallbackSelection(rawText ?? string.Empty);
        }

        return parsed;
    }

    private string BuildSystemPrompt(IEnumerable<Persona> personas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the selector for an advisor council that examines questions from multiple framings.");
        sb.AppendLine("Decide which personas should participate based on what the question needs.");
        sb.AppendLine();
        sb.AppendLine("Available personas:");
        foreach (var p in personas.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            sb.Append("- ").Append(p.Id).Append(" — ").AppendLine(p.Description);
        }
        sb.AppendLine();
        sb.AppendLine("Selection guidance:");
        sb.AppendLine("- Pick 3–5 personas whose framings are most relevant. Do not select all by default.");
        sb.AppendLine("- pre_research: true only when multiple personas would benefit from a shared factual base (specific technologies, companies, recent events).");
        sb.AppendLine("- critique: true when the question is contested or strategic and personas are likely to disagree. False for mostly factual integration.");
        sb.AppendLine("- needs_research per persona: true when the persona's framing requires up-to-date facts (e.g. engineer, economist) AND research is relevant to this question.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only, in this exact shape:");
        sb.AppendLine(@"{ ""personas"": [{""id"": ""..."", ""needs_research"": false}], ""pre_research"": false, ""critique"": false, ""rationale"": ""..."" }");
        return sb.ToString();
    }

    private static SelectorOutput? ParseAndValidate(string raw, HashSet<string> knownPersonaIds)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJson(raw);
        if (json is null) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<SelectorOutput>(json, JsonOpts);
            if (parsed is null) return null;
            if (parsed.Personas is null || parsed.Personas.Count == 0) return null;
            var filtered = parsed.Personas
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && knownPersonaIds.Contains(p.Id))
                .ToList();
            if (filtered.Count == 0) return null;
            return parsed with { Personas = filtered };
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

    private SelectorOutput FallbackSelection(string rawText)
    {
        var defaults = new[] { "skeptic", "engineer", "long_term" };
        var available = registry.Personas;
        var selected = defaults
            .Where(id => available.ContainsKey(id))
            .Select(id => new SelectedPersona(id, false))
            .ToList();
        if (selected.Count == 0)
        {
            selected = available.Keys
                .Take(3)
                .Select(id => new SelectedPersona(id, false))
                .ToList();
        }
        return new SelectorOutput(selected, false, false, "Fallback default selection — selector output failed schema validation.");
    }

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty :
        s.Length <= max ? s : s[..max] + "…";
}
