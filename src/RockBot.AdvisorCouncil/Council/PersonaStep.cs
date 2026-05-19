using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Host;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Runs one persona view. Reads any shared pre-research findings from working memory
/// at <c>council/{taskId}/shared</c>, exposes a scoped <c>research</c> tool the persona
/// can invoke autonomously, and writes its final view to <c>council/{taskId}/{personaId}/view</c>.
/// Per-persona research calls land at <c>council/{taskId}/{personaId}/research/{n}</c>.
/// </summary>
internal sealed class PersonaStep(
    IChatClient chatClient,
    ResearchAgentInvoker invoker,
    IWorkingMemory workingMemory,
    IOptions<CouncilOptions> options,
    ILogger<PersonaStep> logger)
{
    public async Task<PersonaView> RunAsync(
        Persona persona,
        string question,
        string taskId,
        CancellationToken ct)
    {
        var shared = await workingMemory.GetAsync($"council/{taskId}/shared");
        var userPrompt = shared is null
            ? question
            : $"Use the following pre-research findings as context. Do not contradict facts in them.\n\n--- Pre-research findings ---\n{shared}\n--- End findings ---\n\nQuestion: {question}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, persona.SystemPrompt),
            new(ChatRole.User, userPrompt)
        };

        // Lambda (not method group) so the invoker reference is only dereferenced when the
        // tool is actually invoked. Tests that don't exercise the research path can pass
        // null! for the invoker without constructing one.
        var scopedTool = new PersonaScopedResearchTool(
            research: (q, c) => invoker.InvokeAsync(q, c),
            wm: workingMemory,
            taskId: taskId,
            personaId: persona.Id,
            maxCalls: options.Value.MaxResearchCallsPerPersona,
            logger: logger);
        var chatOptions = new ChatOptions { Tools = [scopedTool] };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
            var text = response.Text?.Trim() ?? string.Empty;

            try
            {
                await workingMemory.SetAsync(
                    key: $"council/{taskId}/{persona.Id}/view",
                    value: text,
                    ttl: TimeSpan.FromMinutes(30),
                    category: "council/view",
                    tags: [persona.Id, taskId]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write persona view to WM for {Persona}", persona.Id);
            }

            return new PersonaView(persona.Id, text, [], [], PersonaStatus.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PersonaStep failed for persona {Id}", persona.Id);
            return new PersonaView(persona.Id, "(persona call failed)", [], [], PersonaStatus.Failed);
        }
    }

    /// <summary>
    /// Per-call wrapper around the research invocation delegate. Enforces the per-persona
    /// research budget and persists successful results to working memory under
    /// <c>council/{taskId}/{personaId}/research/{n}</c>. Takes a delegate rather than the
    /// concrete invoker so tests can supply a stub without InProcess messaging setup.
    /// </summary>
    internal sealed class PersonaScopedResearchTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{"type":"object","properties":{"question":{"type":"string","description":"The research question."}},"required":["question"]}""")
            .RootElement;

        private readonly Func<string, CancellationToken, Task<string>> _research;
        private readonly IWorkingMemory _wm;
        private readonly string _taskId;
        private readonly string _personaId;
        private readonly int _maxCalls;
        private readonly ILogger _logger;
        private int _callCount;

        public PersonaScopedResearchTool(
            Func<string, CancellationToken, Task<string>> research,
            IWorkingMemory wm,
            string taskId,
            string personaId,
            int maxCalls,
            ILogger logger)
        {
            _research = research;
            _wm = wm;
            _taskId = taskId;
            _personaId = personaId;
            _maxCalls = maxCalls;
            _logger = logger;
        }

        public override string Name => "research";

        public override string Description =>
            "Search the web and summarise findings on a focused question. Each call is delegated " +
            "to a research agent and costs latency, so use sparingly and only when current facts " +
            "would sharpen your view from your persona's lens. Pass one focused question per call.";

        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            string? question = null;
            if (arguments.TryGetValue("question", out var v))
                question = v?.ToString();
            if (string.IsNullOrWhiteSpace(question))
                return "Error: missing required argument 'question'.";

            var n = Interlocked.Increment(ref _callCount);
            if (n > _maxCalls)
            {
                _logger.LogInformation(
                    "Persona {Persona} on council {Task} exhausted research budget ({Max} calls); returning sentinel",
                    _personaId, _taskId, _maxCalls);
                return $"(research budget exhausted: this persona has already made {_maxCalls} research call(s) — answer from existing context)";
            }

            var findings = await _research(question!, cancellationToken);

            // Skip writing failure sentinels emitted by ResearchAgentInvoker (e.g. "(research failed: ...)",
            // "(research timed out)", "(research call failed: ...)") so the WM pool stays signal-rich.
            if (!string.IsNullOrWhiteSpace(findings) &&
                !findings.StartsWith("(research", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _wm.SetAsync(
                        key: $"council/{_taskId}/{_personaId}/research/{n}",
                        value: $"Q: {question}\n\n{findings}",
                        ttl: TimeSpan.FromMinutes(30),
                        category: "council/research",
                        tags: [_personaId, _taskId]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write research result to WM for {Persona}", _personaId);
                }
            }

            return findings;
        }
    }
}
