using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Name-based classification of whether a tool call changes state outside the agent — writes
/// or deletes a file, uploads, sends, creates or updates a record. Used by mid-turn tier
/// escalation (#663): a turn routed Low that starts making such calls moves to Balanced.
/// <para>
/// There is no MCP-annotation plumbing to reuse — the bridge does not carry
/// <c>readOnlyHint</c>/<c>destructiveHint</c> through the tool registry — so this reads the
/// verb in the tool's name (<c>file_write</c>, <c>sharepoint_upload_file</c>,
/// <c>outlook_send_mail</c>, <c>update_task</c>). The first recognised verb token decides:
/// <c>get_run_status</c> reads, <c>outlook_create_reply_draft</c> writes. A name with no
/// recognised verb is treated as read-only. For <c>mcp_invoke_tool</c> the inner
/// <c>tool_name</c> argument is classified instead.
/// </para>
/// <para>
/// The agent's own bookkeeping — memory, working memory, the task list, progress reports,
/// subagent delegation — is exempt: it changes nothing the user would call a side effect,
/// and a small model saving a memory is not a reason to move up a tier.
/// </para>
/// </summary>
public static class ToolSideEffects
{
    private static readonly HashSet<string> ExemptTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // Long-term and working memory
        "save_memory", "edit_memory", "delete_memory", "update_memory_importance", "memory",
        "save_to_working_memory", "edit_working_memory", "delete_from_working_memory",
        // Per-run task list and progress
        "task_create", "task_update", "report_progress", "feedback",
        // Reply staging, session settings, delegation, hand-back answers
        "attach_image", "set_timezone", "spawn_subagent", "cancel_subagent", "invoke_agent",
        "spawn_workers", "mcp_answer",
    };

    private static readonly HashSet<string> AlwaysSideEffecting = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write", "file_edit", "file_delete", "file_move", "file_rename", "file_copy",
    };

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "get", "list", "search", "read", "find", "fetch", "query", "describe", "check", "view",
        "download", "lookup", "look", "count", "show", "inspect", "analyze", "analyse", "preview",
        "browse", "resolve", "validate", "peek", "stat", "summarize", "summarise", "explain",
        "whoami", "ping", "status", "info", "scan", "load", "recall", "retrieve", "estimate",
        "render", "convert", "translate", "compare", "diff", "test",
    };

    private static readonly HashSet<string> WriteVerbs = new(StringComparer.Ordinal)
    {
        "write", "edit", "update", "create", "delete", "remove", "move", "rename", "upload",
        "send", "post", "put", "patch", "insert", "append", "replace", "set", "save", "add",
        "cancel", "publish", "share", "unshare", "modify", "overwrite", "upsert", "trash",
        "untrash", "archive", "unarchive", "reply", "forward", "complete", "uncomplete",
        "restore", "mark", "assign", "unassign", "invite", "accept", "decline", "respond",
        "schedule", "reschedule", "book", "transfer", "copy", "import", "submit", "approve",
        "reject", "merge", "push", "commit", "deploy", "execute", "run", "apply", "enable",
        "disable", "start", "stop", "restart", "kill", "drop", "purge", "clear", "reset",
        "revoke", "grant", "subscribe", "unsubscribe", "label", "unlabel", "tag", "untag",
        "close", "promote", "install", "uninstall", "register", "unregister", "store", "change",
    };

    private static readonly Regex TokenRegex = new(
        @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+",
        RegexOptions.Compiled);

    /// <summary>
    /// True when a call to <paramref name="toolName"/> with <paramref name="arguments"/>
    /// changes external state. <paramref name="effectiveName"/> is the name that was
    /// classified — the inner tool for <c>mcp_invoke_tool</c>.
    /// </summary>
    public static bool IsSideEffecting(
        string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        out string effectiveName)
    {
        effectiveName = toolName;
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (string.Equals(toolName, "mcp_invoke_tool", StringComparison.OrdinalIgnoreCase))
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

        return IsSideEffectingName(toolName);
    }

    /// <summary>True when the tool name alone marks a side-effecting call.</summary>
    public static bool IsSideEffectingName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName) || ExemptTools.Contains(toolName))
            return false;

        if (AlwaysSideEffecting.Contains(toolName))
            return true;

        foreach (Match m in TokenRegex.Matches(toolName))
        {
            var token = m.Value.ToLowerInvariant();
            if (ReadVerbs.Contains(token))
                return false;
            if (WriteVerbs.Contains(token))
                return true;
        }

        return false;
    }
}
