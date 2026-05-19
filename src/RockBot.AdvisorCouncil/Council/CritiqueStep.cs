using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;
using RockBot.Host;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// For each persona, runs a second IChatClient call that revises the persona's view in
/// light of sibling views and names explicit tensions. Reads accumulated research findings
/// from working memory under <c>council/{taskId}/</c> so the rebuttal round can reference
/// evidence any persona has surfaced. Per-persona parallel.
/// </summary>
internal sealed class CritiqueStep(
    IChatClient chatClient,
    IWorkingMemory workingMemory,
    ILogger<CritiqueStep> logger)
{
    private const int MaxResearchSnippetChars = 300;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed record CritiqueOutput(PersonaView RevisedView, IReadOnlyList<Tension> Tensions);

    public async Task<CritiqueOutput> RunAsync(
        Persona persona,
        string question,
        string taskId,
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

        var researchPool = await BuildResearchPoolAsync(taskId, ct);
        var user = BuildUserPrompt(question, ownView, siblingViews, researchPool);

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
            {
                // Critique can revise the view text but does not change whether the persona
                // contributed: carry forward the original status (ok/timed_out/failed).
                var preserved = parsed with
                {
                    RevisedView = parsed.RevisedView with { Status = ownView.Status }
                };

                try
                {
                    await workingMemory.SetAsync(
                        key: $"council/{taskId}/{persona.Id}/view-revised",
                        value: preserved.RevisedView.View,
                        ttl: TimeSpan.FromMinutes(30),
                        category: "council/view",
                        tags: [persona.Id, taskId, "revised"]);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to write revised view to WM for {Persona}", persona.Id);
                }
                return preserved;
            }
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

    private static string BuildUserPrompt(
        string question,
        PersonaView ownView,
        IReadOnlyList<PersonaView> siblings,
        IReadOnlyList<(string Key, string Snippet)> researchPool)
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

        if (researchPool.Count > 0)
        {
            sb.AppendLine("Research findings available to the council (use any that sharpen your dissent or concurrence):");
            foreach (var (key, snippet) in researchPool)
            {
                sb.Append("### ").AppendLine(key);
                sb.AppendLine(snippet).AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Lists working-memory entries under the council's task namespace and selects only
    /// research-type keys (shared baseline + per-persona research calls), truncating each
    /// snippet to keep the prompt bounded.
    /// </summary>
    private async Task<IReadOnlyList<(string Key, string Snippet)>> BuildResearchPoolAsync(string taskId, CancellationToken ct)
    {
        try
        {
            var entries = await workingMemory.ListAsync($"council/{taskId}/");
            var pool = new List<(string Key, string Snippet)>();
            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) break;
                if (!IsResearchKey(entry.Key)) continue;
                var snippet = entry.Value.Length <= MaxResearchSnippetChars
                    ? entry.Value
                    : entry.Value[..MaxResearchSnippetChars] + "…";
                pool.Add((entry.Key, snippet));
            }
            return pool;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate WM research pool for task {TaskId}", taskId);
            return [];
        }
    }

    private static bool IsResearchKey(string key)
    {
        if (key.EndsWith("/shared", StringComparison.Ordinal)) return true;
        var researchIdx = key.IndexOf("/research/", StringComparison.Ordinal);
        return researchIdx > 0;
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
