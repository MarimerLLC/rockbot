using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Runs the council pipeline:
/// Select → optional PreResearch → Fan-out personas (parallel) → optional Critique
/// (parallel per persona) → Synthesize.
///
/// Imperative orchestration; the design doc explicitly permits this in lieu of MAF
/// workflows when dynamic-graph-per-request negates static-graph value.
/// </summary>
internal sealed class CouncilOrchestrator(
    SelectStep selectStep,
    PreResearchStep preResearchStep,
    PersonaStep personaStep,
    CritiqueStep critiqueStep,
    SynthesizeStep synthesizeStep,
    PersonaRegistry personaRegistry,
    IOptions<CouncilOptions> options,
    ILogger<CouncilOrchestrator> logger)
{
    public async Task<CouncilResponse> RunAsync(string question, string taskId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var modelCalls = 0;
        var personas = personaRegistry.Personas;

        // ── 1. Select ──────────────────────────────────────────────────────────
        var selection = await selectStep.RunAsync(question, ct);
        modelCalls++;
        logger.LogInformation(
            "Council task {TaskId}: selected {Count} personas (preResearch={Pre}, critique={Crit}). Rationale: {Rat}",
            taskId, selection.Personas.Count, selection.PreResearch, selection.Critique, selection.Rationale);

        var selectedPersonas = selection.Personas
            .Where(p => personas.ContainsKey(p.Id))
            .Select(p => (Spec: p, Persona: personas[p.Id]))
            .ToList();

        if (selectedPersonas.Count == 0)
            return EmptyResponse(question, "No personas selected. The persona registry may be empty or the selector returned no valid ids.", sw.Elapsed, modelCalls);

        // ── 2. PreResearch (conditional) ───────────────────────────────────────
        string? preResearch = null;
        if (selection.PreResearch)
        {
            preResearch = await preResearchStep.RunAsync(question, ct);
            if (preResearch is not null) modelCalls++;
        }

        // ── 3. Fan-out personas in parallel ────────────────────────────────────
        var perPersonaTimeout = TimeSpan.FromSeconds(Math.Max(5, options.Value.PerPersonaTimeoutSeconds));
        var personaTasks = selectedPersonas.Select(async sp =>
        {
            using var personaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            personaCts.CancelAfter(perPersonaTimeout);
            try
            {
                return await personaStep.RunAsync(sp.Persona, question, preResearch, sp.Spec.NeedsResearch, personaCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Persona {Id} timed out after {Sec}s", sp.Spec.Id, perPersonaTimeout.TotalSeconds);
                return new PersonaView(sp.Spec.Id, "(timed out)", [], []);
            }
        }).ToList();

        var personaViews = await Task.WhenAll(personaTasks);
        modelCalls += personaViews.Length; // approximate — research path may do more calls

        // ── 4. Critique (conditional, per-persona parallel) ────────────────────
        var critiqueTensions = new List<Tension>();
        if (selection.Critique && personaViews.Length > 1)
        {
            var critiqueTasks = personaViews.Select(async (own, idx) =>
            {
                var persona = selectedPersonas[idx].Persona;
                var siblings = personaViews.Where((_, i) => i != idx).ToList();
                using var critCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                critCts.CancelAfter(perPersonaTimeout);
                try
                {
                    return await critiqueStep.RunAsync(persona, question, own, siblings, critCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Critique for persona {Id} timed out", own.Id);
                    return new CritiqueStep.CritiqueOutput(own, []);
                }
            }).ToList();

            var critiqueResults = await Task.WhenAll(critiqueTasks);
            modelCalls += critiqueResults.Length;

            for (var i = 0; i < critiqueResults.Length; i++)
                personaViews[i] = critiqueResults[i].RevisedView;
            critiqueTensions.AddRange(critiqueResults.SelectMany(r => r.Tensions));
        }

        // ── 5. Synthesize ──────────────────────────────────────────────────────
        var synthesis = await synthesizeStep.RunAsync(
            new SynthesizeStep.SynthesisInput(question, personaViews, critiqueTensions),
            ct);
        modelCalls++;

        sw.Stop();

        return new CouncilResponse(
            Question: question,
            Personas: personaViews,
            Tensions: synthesis.Tensions,
            Synthesis: synthesis.Synthesis,
            Confidence: synthesis.Confidence,
            Metadata: new CouncilMetadata(
                CritiqueRun: selection.Critique && personaViews.Length > 1,
                PreResearchRun: preResearch is not null,
                PersonaCount: personaViews.Length,
                DurationMs: sw.ElapsedMilliseconds,
                ModelCalls: modelCalls,
                PersonaSetHash: personaRegistry.PersonaSetHash,
                SelectorRationale: selection.Rationale));
    }

    private CouncilResponse EmptyResponse(string question, string reason, TimeSpan duration, int modelCalls) =>
        new(
            Question: question,
            Personas: [],
            Tensions: [],
            Synthesis: reason,
            Confidence: "low",
            Metadata: new CouncilMetadata(
                CritiqueRun: false,
                PreResearchRun: false,
                PersonaCount: 0,
                DurationMs: (long)duration.TotalMilliseconds,
                ModelCalls: modelCalls,
                PersonaSetHash: personaRegistry.PersonaSetHash,
                SelectorRationale: reason));
}
