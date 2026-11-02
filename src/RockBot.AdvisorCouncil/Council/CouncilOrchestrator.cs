using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;
using RockBot.Host;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// The council, in the two halves the <c>advise</c> tool needs: Select (cheap, and the only place
/// a clarifying question can come from) and the deliberation that follows.
/// </summary>
internal interface ICouncil
{
    /// <summary>Step 1: which personas, whether to pre-research and critique, and what is missing.</summary>
    Task<SelectorOutput> SelectAsync(CouncilRequest request, CancellationToken ct);

    /// <summary>Steps 2–5 for a selection already made.</summary>
    Task<CouncilResponse> DeliberateAsync(CouncilRequest request, SelectorOutput selection, string runId, CancellationToken ct);
}

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
    ILogger<CouncilOrchestrator> logger,
    IWorkingMemory? workingMemory = null) : ICouncil
{
    /// <summary>Select, then deliberate, with no question in between.</summary>
    public async Task<CouncilResponse> RunAsync(CouncilRequest request, string runId, CancellationToken ct)
    {
        var selection = await SelectAsync(request, ct);
        return await DeliberateAsync(request with { Unresolved = selection.Missing ?? [] }, selection, runId, ct);
    }

    public async Task<SelectorOutput> SelectAsync(CouncilRequest request, CancellationToken ct)
    {
        var selection = await selectStep.RunAsync(request, ct);
        logger.LogInformation(
            "Council: selected {Count} personas (preResearch={Pre}, critique={Crit}, missing=[{Missing}], tooThin={Thin}). Rationale: {Rat}",
            selection.Personas.Count, selection.PreResearch, selection.Critique,
            string.Join(",", selection.Missing ?? []), selection.TooThin, selection.Rationale);
        return selection;
    }

    public async Task<CouncilResponse> DeliberateAsync(
        CouncilRequest request, SelectorOutput selection, string runId, CancellationToken ct)
    {
        try
        {
            return await DeliberateCoreAsync(request, selection, runId, ct);
        }
        finally
        {
            // A long-lived server runs many councils; each one's working memory goes when it ends.
            if (workingMemory is not null)
            {
                try
                {
                    await workingMemory.ClearAsync($"council/{runId}/");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Council {RunId}: could not clear its working memory", runId);
                }
            }
        }
    }

    private async Task<CouncilResponse> DeliberateCoreAsync(
        CouncilRequest request, SelectorOutput selection, string runId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var modelCalls = 1; // Select
        var personas = personaRegistry.Personas;

        var selectedPersonas = selection.Personas
            .Where(p => personas.ContainsKey(p.Id))
            .Select(p => personas[p.Id])
            .ToList();

        if (selectedPersonas.Count == 0)
            return EmptyResponse(request, "No personas selected. The persona registry may be empty or the selector returned no valid ids.", sw.Elapsed, modelCalls);

        // ── 2. PreResearch (conditional) ───────────────────────────────────────
        // Persona-aware research that writes to WM at council/{runId}/shared; personas
        // read from there in step 3. The boolean tells us whether to flag preResearchRun
        // in metadata.
        var preResearchRun = false;
        if (selection.PreResearch)
        {
            preResearchRun = await preResearchStep.RunAsync(request, selectedPersonas, runId, ct);
            if (preResearchRun) modelCalls++;
        }

        // ── 3. Fan-out personas in parallel ────────────────────────────────────
        var perPersonaTimeout = TimeSpan.FromSeconds(Math.Max(5, options.Value.PerPersonaTimeoutSeconds));
        var personaTasks = selectedPersonas.Select(async persona =>
        {
            using var personaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            personaCts.CancelAfter(perPersonaTimeout);
            try
            {
                return await personaStep.RunAsync(persona, request, runId, personaCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Persona {Id} timed out after {Sec}s", persona.Id, perPersonaTimeout.TotalSeconds);
                return new PersonaView(persona.Id, "(timed out)", [], [], PersonaStatus.TimedOut);
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
                var persona = selectedPersonas[idx];
                var siblings = personaViews.Where((_, i) => i != idx).ToList();
                using var critCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                critCts.CancelAfter(perPersonaTimeout);
                try
                {
                    return await critiqueStep.RunAsync(persona, request, runId, own, siblings, critCts.Token);
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
            new SynthesizeStep.SynthesisInput(request, personaViews, critiqueTensions),
            ct);
        modelCalls++;

        sw.Stop();

        var contributedCount = personaViews.Count(v => v.Status == PersonaStatus.Ok);
        var missingIds = personaViews
            .Where(v => v.Status != PersonaStatus.Ok)
            .Select(v => v.Id)
            .ToList();

        return new CouncilResponse(
            Question: request.Question,
            Personas: personaViews,
            Tensions: synthesis.Tensions,
            Synthesis: synthesis.Synthesis,
            Confidence: synthesis.Confidence,
            Metadata: new CouncilMetadata(
                CritiqueRun: selection.Critique && personaViews.Length > 1,
                PreResearchRun: preResearchRun,
                PersonaCount: personaViews.Length,
                DurationMs: sw.ElapsedMilliseconds,
                ModelCalls: modelCalls,
                PersonaSetHash: personaRegistry.PersonaSetHash,
                SelectorRationale: selection.Rationale,
                PersonasContributed: contributedCount,
                PersonasMissing: missingIds,
                Clarification: request.Clarification),
            Assumptions: synthesis.Assumptions ?? []);
    }

    private CouncilResponse EmptyResponse(CouncilRequest request, string reason, TimeSpan duration, int modelCalls) =>
        new(
            Question: request.Question,
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
                SelectorRationale: reason,
                Clarification: request.Clarification),
            Assumptions: []);
}
