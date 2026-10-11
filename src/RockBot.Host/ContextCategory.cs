using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Names for the parts of an LLM request's message list, used by the per-request context
/// accounting (#670) to report where the input tokens go.
/// </summary>
public static class ContextCategory
{
    public const string SystemPrompt = "system-prompt";
    public const string ClientCapabilities = "client-capabilities";
    public const string Rules = "rules";
    public const string ModelBehavior = "model-behavior";
    public const string SessionStart = "session-start";
    public const string HistoryUser = "history-user";
    public const string HistoryAssistant = "history-assistant";
    public const string SubagentRelay = "subagent-relay";
    public const string UserMessage = "user-message";
    public const string Attachments = "attachments";
    public const string LongTermMemory = "ltm-recall";
    public const string Episodic = "episodic";
    public const string Identity = "identity";
    public const string KnowledgeGraph = "knowledge-graph";
    public const string SkillIndex = "skill-index";
    public const string SkillBody = "skill-body";
    public const string SkillSummaries = "skill-summaries";
    public const string ServiceHints = "service-hints";
    public const string WorkingMemory = "working-memory";
    public const string PatrolInventory = "patrol-inventory";
    public const string SharedWorkingMemory = "shared-working-memory";
    public const string SubagentResearch = "subagent-research";
    public const string WorkProducts = "work-products";
    public const string Lineage = "lineage";
    public const string McpOrientation = "mcp-orientation";
    public const string DeferredTools = "deferred-tools";
    public const string DateTime = "datetime";
    public const string Scaffolding = "scaffolding";
    public const string TaskList = "task-list";
    public const string StashRegistry = "stash-registry";
    public const string ToolCalls = "tool-calls";
    public const string ToolResults = "tool-results";
    public const string LoopAssistant = "loop-assistant";
    public const string LoopUser = "loop-user";
    public const string SystemOther = "system-other";
}

/// <summary>
/// Labels a <see cref="ChatMessage"/> with the <see cref="ContextCategory"/> it belongs to.
/// The label lives in a weak side table, never on the message itself, so it can't reach
/// the provider and is collected with the message.
/// </summary>
public static class ContextTags
{
    private static readonly ConditionalWeakTable<ChatMessage, string> Tags = new();

    /// <summary>Labels <paramref name="message"/> and returns it, for use inline at the injection site.</summary>
    public static ChatMessage Tag(this ChatMessage message, string category)
    {
        Tags.AddOrUpdate(message, category);
        return message;
    }

    /// <summary>The label given to <paramref name="message"/>, or null when it was never tagged.</summary>
    public static string? Get(ChatMessage message) =>
        Tags.TryGetValue(message, out var category) ? category : null;
}

/// <summary>
/// Assigns each message of a request to a <see cref="ContextCategory"/>: a tagged message keeps its
/// tag; anything else is classified from its role and its position relative to the latest user message.
/// </summary>
public static class ContextCategorizer
{
    /// <summary>Opening of the synthetic user turn that relays a subagent's result.</summary>
    internal const string SubagentRelayPrefix = "[Subagent task ";

    public static string[] Classify(IReadOnlyList<ChatMessage> messages)
    {
        var lastUserIdx = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                lastUserIdx = i;
                break;
            }
        }

        var categories = new string[messages.Count];
        for (var i = 0; i < messages.Count; i++)
            categories[i] = Classify(messages[i], i, lastUserIdx);
        return categories;
    }

    internal static string Classify(ChatMessage message, int index, int lastUserIdx)
    {
        if (message.Role == ChatRole.Tool || message.Contents.Any(c => c is FunctionResultContent))
            return ContextCategory.ToolResults;
        if (message.Contents.Any(c => c is FunctionCallContent))
            return ContextCategory.ToolCalls;

        var tag = ContextTags.Get(message);
        if (tag == ContextCategory.HistoryUser && index == lastUserIdx)
            return ContextCategory.UserMessage;
        if (tag is not null)
            return tag;

        if (message.Role == ChatRole.System)
            return index == 0 ? ContextCategory.SystemPrompt : ContextCategory.SystemOther;

        var isRelay = message.Role == ChatRole.User
            && (message.Text?.StartsWith(SubagentRelayPrefix, StringComparison.Ordinal) ?? false);

        if (index < lastUserIdx)
        {
            if (isRelay) return ContextCategory.SubagentRelay;
            return message.Role == ChatRole.User ? ContextCategory.HistoryUser : ContextCategory.HistoryAssistant;
        }
        if (index == lastUserIdx)
            return isRelay ? ContextCategory.SubagentRelay : ContextCategory.UserMessage;
        return message.Role == ChatRole.User ? ContextCategory.LoopUser : ContextCategory.LoopAssistant;
    }
}
