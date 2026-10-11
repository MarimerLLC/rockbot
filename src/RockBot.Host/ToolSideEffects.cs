using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Conservative name-based classification of whether a tool call changes something outside the
/// agent's own scratch state — a file, a calendar, a mailbox, an MCP server's data. Neither the
/// tool registry nor the MCP catalog keeps a read-only/destructive annotation, so this reads the
/// verb in the tool's name. Shared by the completion evaluator's "did this turn change an
/// artifact?" trigger (#666) and mid-turn tier escalation (#663).
/// </summary>
/// <remarks>
/// Rules, in order:
/// <list type="number">
///   <item>Generic proxies are unwrapped: <c>mcp_invoke_tool</c> is classified by its
///   <c>tool_name</c> argument, a typed wrapper <c>{server}__{tool}</c> by its tool part.</item>
///   <item>The agent's own bookkeeping (task list, working memory, long-term memory, progress
///   reports, session settings, the MCP hand-back answer) and delegation never count — they change
///   nothing the user asked for, and counting them would run the evaluator (or escalate the tier)
///   on every turn that saves a memory.</item>
///   <item>A name whose first word is a read verb (<c>get</c>, <c>list</c>, <c>search</c>, …) is
///   read-only, so <c>get_file_base64</c> or <c>list_completed</c> do not count.</item>
///   <item>Otherwise the call is side-effecting when any word of the name is a write verb
///   (<c>write</c>, <c>edit</c>, <c>update</c>, <c>delete</c>, <c>upload</c>, <c>send</c>, …).</item>
/// </list>
/// Unknown names with no write verb are treated as read-only: the gate errs toward not spending
/// an evaluator call or a tier escalation, and other triggers still cover a turn that claims work
/// it didn't do.
/// </remarks>
public static partial class ToolSideEffects
{
    /// <summary>The generic MCP proxy whose real target is in its <c>tool_name</c> argument.</summary>
    public const string McpInvokeToolName = "mcp_invoke_tool";

    private const string TypedToolSeparator = "__";

    /// <summary>
    /// The agent's own bookkeeping tools. They write, but only to the agent's scratch, memory or
    /// session state — never to an artifact the user is waiting on.
    /// </summary>
    private static readonly HashSet<string> BookkeepingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "task_create", "task_update",
        "report_progress", "feedback",
        "save_to_working_memory", "edit_working_memory", "delete_from_working_memory",
        "save_memory", "edit_memory", "delete_memory", "update_memory_importance", "memory",
        "resume_memory_consolidation",
        "set_timezone",
        "cancel_subagent", "spawn_workers",
        "mcp_answer",
    };

    /// <summary>
    /// Tools that start work whose result arrives later. Not side-effecting themselves; the
    /// evaluator skips the spawning loop and checks the synthesis turn instead.
    /// </summary>
    private static readonly HashSet<string> DelegationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn_subagent", "invoke_agent",
    };

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "list", "search", "read", "find", "fetch", "query", "describe", "lookup", "look",
        "check", "view", "browse", "analyze", "analyse", "inspect", "count", "show", "preview",
        "download", "export", "peek", "stat", "resolve", "validate", "estimate", "current",
        "summarize", "summarise", "explain", "whoami", "ping", "status", "info", "scan", "load",
        "recall", "retrieve", "render", "convert", "translate", "compare", "diff", "test",
    };

    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "update", "create", "delete", "remove", "move", "rename", "copy",
        "upload", "send", "post", "put", "patch", "set", "add", "insert", "append", "save",
        "cancel", "schedule", "reschedule", "publish", "reply", "forward", "share", "unshare",
        "trash", "untrash", "archive", "unarchive", "apply", "modify", "replace", "merge",
        "commit", "push", "deploy", "respond", "accept", "decline", "mark", "label", "unlabel",
        "assign", "unassign", "complete", "uncomplete", "restore", "register", "unregister",
        "import", "reset", "drop", "truncate", "overwrite", "upsert", "attach", "submit", "book",
        "invite", "promote", "draft", "rewrite", "revise", "execute", "run", "clear", "approve",
        "reject", "transfer", "pay", "purchase", "order", "enable", "disable", "start", "stop",
        "restart", "kill", "purge", "revoke", "grant", "subscribe", "unsubscribe", "tag", "untag",
        "close", "install", "uninstall", "store", "change",
    };

    /// <summary>True when the call is one the agent uses to delegate (<c>spawn_subagent</c>, <c>invoke_agent</c>).</summary>
    public static bool IsDelegation(string toolName) => DelegationTools.Contains(toolName);

    /// <summary>True when the call is the agent's own bookkeeping (task list, memory, progress).</summary>
    public static bool IsBookkeeping(string toolName) => BookkeepingTools.Contains(toolName);

    /// <summary>
    /// True when <paramref name="call"/> changed something outside the agent's own state, or tried
    /// to. A batch tool such as <c>spawn_wisps</c> counts as changing state when any call it made
    /// on the agent's behalf (<see cref="LoopToolCall.Nested"/>, #686) does.
    /// </summary>
    public static bool IsSideEffecting(LoopToolCall call) =>
        IsSideEffecting(call.Name, call.Arguments)
        || (call.Nested?.Any(IsSideEffecting) ?? false);

    /// <summary>
    /// True when a call to <paramref name="toolName"/> with <paramref name="arguments"/> (a
    /// <c>key=value</c> summary or JSON, may be null) changed something outside the agent's own state.
    /// </summary>
    public static bool IsSideEffecting(string toolName, string? arguments = null)
        => IsSideEffectingName(EffectiveToolName(toolName, arguments));

    /// <summary>
    /// True when a call to <paramref name="toolName"/> with structured <paramref name="arguments"/>
    /// changes external state. <paramref name="effectiveName"/> is the name that was classified —
    /// the inner tool for <c>mcp_invoke_tool</c>, the tool part of a typed wrapper.
    /// </summary>
    public static bool IsSideEffecting(
        string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        out string effectiveName)
    {
        effectiveName = toolName;
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (string.Equals(toolName, McpInvokeToolName, StringComparison.OrdinalIgnoreCase))
        {
            var inner = arguments?.FirstOrDefault(a =>
                string.Equals(a.Key, "tool_name", StringComparison.OrdinalIgnoreCase)).Value;
            var innerName = inner switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                _ => inner?.ToString(),
            };
            if (string.IsNullOrWhiteSpace(innerName))
                return false;
            effectiveName = innerName;
            return IsSideEffectingName(innerName);
        }

        effectiveName = EffectiveToolName(toolName, arguments: null);
        return IsSideEffectingName(effectiveName);
    }

    /// <summary>True when the tool name alone (already unwrapped) marks a side-effecting call.</summary>
    public static bool IsSideEffectingName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        if (IsBookkeeping(toolName) || IsDelegation(toolName)) return false;

        var words = SplitWords(toolName);
        if (words.Count == 0) return false;
        if (ReadVerbs.Contains(words[0])) return false;
        return words.Any(WriteVerbs.Contains);
    }

    /// <summary>
    /// The name a call should be judged by: the <c>tool_name</c> argument of <c>mcp_invoke_tool</c>,
    /// the tool part of a typed <c>{server}__{tool}</c> wrapper, or the name itself.
    /// </summary>
    public static string EffectiveToolName(string toolName, string? arguments)
    {
        if (string.Equals(toolName, McpInvokeToolName, StringComparison.OrdinalIgnoreCase))
        {
            var match = arguments is null ? null : InnerToolNameRegex().Match(arguments);
            return match is { Success: true } ? match.Groups["name"].Value : string.Empty;
        }

        var sep = toolName.IndexOf(TypedToolSeparator, StringComparison.Ordinal);
        return sep >= 0 ? toolName[(sep + TypedToolSeparator.Length)..] : toolName;
    }

    /// <summary>Splits <c>snake_case</c>, <c>kebab-case</c>, dotted and <c>camelCase</c> names into lower-case words.</summary>
    internal static List<string> SplitWords(string name)
    {
        var spaced = CamelBoundaryRegex().Replace(name, "$1 $2");
        return spaced
            .Split(['_', '-', '.', ' ', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToList();
    }

    // Matches tool_name=foo (native args summary) and "tool_name":"foo" (JSON).
    [GeneratedRegex(@"tool_name""?\s*[:=]\s*""?(?<name>[A-Za-z0-9_.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnerToolNameRegex();

    [GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundaryRegex();
}
