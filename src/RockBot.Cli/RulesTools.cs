using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Cli;

/// <summary>
/// LLM-callable tools for managing hard behavioral rules.
/// Rules are persisted to disk and injected into every system prompt,
/// giving them the same authority as the agent's directives file.
/// </summary>
internal sealed class RulesTools
{
    private readonly IRulesStore _rulesStore;
    private readonly ILogger<RulesTools> _logger;
    private readonly IList<AITool> _tools;

    public RulesTools(IRulesStore rulesStore, ILogger<RulesTools> logger)
    {
        _rulesStore = rulesStore;
        _logger = logger;

        _tools =
        [
            AIFunctionFactory.Create(AddRule),
            AIFunctionFactory.Create(RemoveRule),
            AIFunctionFactory.Create(ListRules)
        ];
    }

    /// <summary>
    /// Returns the <see cref="AIFunction"/> instances for use in <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IList<AITool> Tools => _tools;

    [Description("Add a hard behavioral rule that will always be enforced, regardless of context or " +
                 "conversation history. Use this when the user wants to permanently shape how you respond — " +
                 "for example, 'only respond in French' or 'never use bullet points'. " +
                 "Rules persist across sessions and cannot be overridden by other instructions.")]
    public async Task<string> AddRule(
        [Description("The rule to enforce, stated as a clear behavioral constraint")] string rule)
    {
        _logger.LogInformation("Tool call: AddRule({Rule})", rule);
        await _rulesStore.AddAsync(rule);
        return $"Rule added: \"{rule}\"";
    }

    [Description("Remove an active behavioral rule. Call list_rules first to see the exact text of " +
                 "current rules — the rule argument must match exactly.")]
    public async Task<string> RemoveRule(
        [Description("The exact text of the rule to remove (use list_rules to find it)")] string rule)
    {
        _logger.LogInformation("Tool call: RemoveRule({Rule})", rule);

        var countBefore = _rulesStore.Rules.Count;
        await _rulesStore.RemoveAsync(rule);
        var countAfter = _rulesStore.Rules.Count;

        return countBefore == countAfter
            ? $"No rule found matching \"{rule}\". Use list_rules to see current rules."
            : $"Rule removed: \"{rule}\"";
    }

    [Description("List all active behavioral rules. Rules are always enforced and persist across sessions.")]
    public async Task<string> ListRules()
    {
        _logger.LogInformation("Tool call: ListRules()");

        var rules = await _rulesStore.ListAsync();

        if (rules.Count == 0)
            return "No active rules. Use add_rule to add one.";

        var sb = new StringBuilder();
        sb.AppendLine($"Active rules ({rules.Count}):");
        foreach (var rule in rules)
            sb.AppendLine($"- {rule}");

        return sb.ToString();
    }
}
