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

    public async Task<SelectorOutput> RunAsync(CouncilRequest request, CancellationToken ct)
    {
        var personas = registry.Personas;
        if (personas.Count == 0)
        {
            logger.LogWarning("No personas loaded — returning empty selection");
            return FallbackSelection(string.Empty);
        }

        var known = personas.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var systemPrompt = BuildSystemPrompt(personas.Values);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, request.RenderBrief())
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
                parsed = ParseAndValidate(rawText, known);
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
                    "Return ONLY a JSON object with keys: personas (array of {id}), " +
                    "pre_research (boolean), critique (boolean), rationale (string), " +
                    "missing (array of strings), too_thin (boolean), needs (array of strings)."));
            }
        }

        if (parsed is null)
        {
            logger.LogWarning("SelectStep falling back to default selection; raw={Raw}", Truncate(rawText, 400));
            parsed = FallbackSelection(rawText ?? string.Empty);
        }

        return ApplyRequest(parsed, request, known);
    }

    /// <summary>
    /// Applies what the caller fixed, in code rather than by asking the model: forced personas,
    /// critique / pre-research set on or off, and only genuinely open choice fields in
    /// <see cref="SelectorOutput.Missing"/>. "Too thin" never stands when the caller gave
    /// <c>context</c>: that is what it would ask for.
    /// </summary>
    internal static SelectorOutput ApplyRequest(SelectorOutput selection, CouncilRequest request, IReadOnlySet<string> knownPersonaIds)
    {
        var forced = request.Personas
            .Where(knownPersonaIds.Contains)
            .Select(id => new SelectedPersona(id))
            .ToList();

        var missing = (selection.Missing ?? [])
            .Select(m => m?.Trim().ToLowerInvariant())
            .Where(m => m is not null && CouncilChoices.AskableFields.Contains(m))
            .Select(m => m!)
            .Where(m => !request.Has(m))
            .Where(m => m != CouncilChoices.PreferredOptionField || request.Options.Count >= 2)
            .Distinct()
            .ToList();

        // Context from the caller is exactly what "too thin" asks for; don't send them round again.
        var tooThin = selection.TooThin && request.Context is null;

        return selection with
        {
            Personas = forced.Count > 0 ? forced : selection.Personas,
            Critique = request.Critique switch
            {
                CouncilToggle.On => true,
                CouncilToggle.Off => false,
                _ => selection.Critique,
            },
            PreResearch = request.PreResearch switch
            {
                CouncilToggle.On => true,
                CouncilToggle.Off => false,
                _ => selection.PreResearch,
            },
            Missing = missing,
            TooThin = tooThin,
            Needs = tooThin ? [.. (selection.Needs ?? []).Where(n => !string.IsNullOrWhiteSpace(n))] : [],
        };
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
        sb.AppendLine("- pre_research: true only when multiple personas would benefit from a shared factual base (specific technologies, companies, recent events). Each persona can also invoke research on its own when its lens demands it, so reserve pre_research for genuinely shared factual ground.");
        sb.AppendLine("- critique: true when the question is contested or strategic and personas are likely to disagree. False for mostly factual integration.");
        sb.AppendLine();
        sb.AppendLine("What is missing:");
        sb.AppendLine("- missing: list only the facts below that are NOT stated in the question or its decision brief AND whose answer would change which personas you pick or what the council would conclude. Usually empty: a sensible assumption beats a question. Allowed values:");
        sb.AppendLine("  - preferred_option: which of the named options the person leans toward (only when two or more options are named)");
        sb.AppendLine("  - decision_kind: strategic, technical, personal or organizational");
        sb.AppendLine("  - time_horizon: weeks, months, years or decade_plus");
        sb.AppendLine("  - risk_appetite: low, moderate or high");
        sb.AppendLine("  - reversibility: easily_reversed, costly_to_reverse or irreversible");
        sb.AppendLine("- too_thin: true ONLY when the question cannot be deliberated on at all without background the person hasn't given (e.g. \"should I do it?\" with nothing saying what \"it\" is). A short but self-contained question is not too thin.");
        sb.AppendLine("- needs: when too_thin, a few short phrases naming the background that is needed (goals, constraints, what is being decided). Empty otherwise.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only, in this exact shape:");
        sb.AppendLine(@"{ ""personas"": [{""id"": ""...""}], ""pre_research"": false, ""critique"": false, ""rationale"": ""..."", ""missing"": [], ""too_thin"": false, ""needs"": [] }");
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
            .Select(id => new SelectedPersona(id))
            .ToList();
        if (selected.Count == 0)
        {
            selected = available.Keys
                .Take(3)
                .Select(id => new SelectedPersona(id))
                .ToList();
        }
        return new SelectorOutput(selected, false, false, "Fallback default selection — selector output failed schema validation.");
    }

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty :
        s.Length <= max ? s : s[..max] + "…";
}
