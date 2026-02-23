using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Agent;

/// <summary>
/// LLM-callable tools for managing hard behavioral rules and persistent agent settings.
/// Rules are persisted to disk and injected into every system prompt,
/// giving them the same authority as the agent's directives file.
/// </summary>
internal sealed class RulesTools
{
    private readonly IRulesStore _rulesStore;
    private readonly AgentClock _clock;
    private readonly ILogger<RulesTools> _logger;
    private readonly IList<AITool> _tools;

    public RulesTools(IRulesStore rulesStore, AgentClock clock, ILogger<RulesTools> logger)
    {
        _rulesStore = rulesStore;
        _clock = clock;
        _logger = logger;

        _tools =
        [
            AIFunctionFactory.Create(AddRule),
            AIFunctionFactory.Create(RemoveRule),
            AIFunctionFactory.Create(ListRules),
            AIFunctionFactory.Create(SetTimezone)
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

    [Description("Update the timezone used for all date and time calculations. Call this when the user " +
                 "says they are in, traveling to, or working from a different timezone. Takes effect " +
                 "immediately and persists across sessions. Use IANA timezone IDs. When the user names " +
                 "a city or region, convert it to the correct IANA ID — e.g. 'Chicago' → " +
                 "'America/Chicago', 'London' → 'Europe/London', 'Tokyo' → 'Asia/Tokyo'.")]
    public async Task<string> SetTimezone(
        [Description("IANA timezone ID, e.g. 'America/New_York', 'Europe/Paris', 'Asia/Singapore'")] string timezoneId)
    {
        _logger.LogInformation("Tool call: SetTimezone({TimezoneId})", timezoneId);

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return $"Unknown timezone '{timezoneId}'. Use an IANA timezone ID such as 'America/Chicago' or 'Europe/London'.";
        }

        await _clock.SetZoneAsync(zone);
        return $"Timezone updated to {zone.DisplayName} ({zone.Id}). All times will now be shown in this timezone.";
    }
}
