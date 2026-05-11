using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Runs one persona view. Phase 1: call-only IChatClient call. Phase 3 expands the
/// agentic-loop branch when <c>needs_research=true</c>.
/// </summary>
internal sealed class PersonaStep(
    IChatClient chatClient,
    ILogger<PersonaStep> logger)
{
    public async Task<PersonaView> RunAsync(
        Persona persona,
        string question,
        string? preResearchFindings,
        bool _needsResearch,
        CancellationToken ct)
    {
        var userPrompt = preResearchFindings is null
            ? question
            : $"Use the following pre-research findings as context. Do not contradict facts in them.\n\n--- Pre-research findings ---\n{preResearchFindings}\n--- End findings ---\n\nQuestion: {question}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, persona.SystemPrompt),
            new(ChatRole.User, userPrompt)
        };

        try
        {
            var response = await chatClient.GetResponseAsync(messages, options: null, ct);
            var text = response.Text?.Trim() ?? string.Empty;
            return new PersonaView(persona.Id, text, [], []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PersonaStep failed for persona {Id}", persona.Id);
            return new PersonaView(persona.Id, "(persona call failed)", [], []);
        }
    }
}
