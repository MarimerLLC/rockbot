using System.Text;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Publishes the memory-audit guide through <c>get_tool_guide</c>, so the agent can explain how
/// memory health is measured — not just report the latest numbers.
/// </summary>
/// <remarks>
/// <para>
/// Complements, rather than replaces, the per-finding explanations the audit tools already
/// return. Those cover "what does this warning mean?", which must be answerable with no extra
/// call because the model may not think to make one. This covers the questions a glossary entry
/// would be the wrong size for: how the measurement works, why memories are retired rather than
/// deleted, what a merge chain is, what the audit deliberately does not claim.
/// </para>
/// <para>
/// Generated from <see cref="MemoryAuditGlossary"/> rather than restating it, so a new invariant
/// cannot appear in the tool output and be missing from the guide.
/// </para>
/// </remarks>
internal sealed class MemoryAuditSkillProvider : IToolSkillProvider
{
    public string Name => "memory-audit";

    public string Summary =>
        "How the agent's memory health is measured, and what every memory-audit warning means in plain language";

    public string GetDocument()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Memory audit");
        sb.AppendLine();
        sb.AppendLine(
            "A scheduled, read-only health check of your own long-term memory. It runs on its own " +
            "(daily by default), looks at the memory files directly, compares them against the " +
            "previous run, and writes a report. It never changes, merges, or deletes a memory.");
        sb.AppendLine();
        sb.AppendLine("## Which tool to use");
        sb.AppendLine();
        sb.AppendLine("- `get_memory_audit` — the latest check: counts, what changed, and any findings.");
        sb.AppendLine("  Each finding already includes a plain-language explanation, so you can answer");
        sb.AppendLine("  \"what does this warning mean?\" straight from the result.");
        sb.AppendLine("- `get_memory_audit_trend(days)` — one row per run, for \"is this getting worse?\"");
        sb.AppendLine("- `get_memory_audit_eval` — a weekly review of whether recent memory decisions");
        sb.AppendLine("  were *good*, not just what they were.");
        sb.AppendLine("- `resume_memory_consolidation` — only when the user explicitly asks to resume.");
        sb.AppendLine();
        sb.AppendLine("Use these for questions about memory *health*. Use `recall` for questions about");
        sb.AppendLine("what you actually remember — recall searches contents, the audit measures the store.");
        sb.AppendLine();

        sb.AppendLine("## How memory changes over time");
        sb.AppendLine();
        sb.AppendLine(
            "Memories are never deleted outright when they are tidied. Two memories that say the " +
            "same thing get **combined** into one, and the originals are **retired** — hidden from " +
            "search but kept on disk for months, with a note saying which memory replaced them. " +
            "Only after that window are they finally removed.");
        sb.AppendLine();
        sb.AppendLine(
            "That is why the audit reports \"live\" and \"archived\" counts separately, and why a " +
            "memory going missing *without* being retired first is the most serious thing it looks " +
            "for. It also means a bad merge normally costs recall, not the fact itself — the " +
            "original is still there to compare against.");
        sb.AppendLine();
        sb.AppendLine(
            "A combined memory can later be combined again. A **merge chain** counts how many " +
            "generations deep that has gone: a chain of 3 means the memory is a summary of a " +
            "summary of a summary. Nothing is necessarily lost, but each generation is another " +
            "rewrite in which a detail can drift.");
        sb.AppendLine();

        sb.AppendLine("## What the findings mean");
        sb.AppendLine();
        sb.AppendLine("Findings are either **alert** (something was destroyed) or **warning** (a limit was");
        sb.AppendLine("crossed, nothing lost). A run with no findings says nothing at all — silence is the");
        sb.AppendLine("healthy state, so no recent warning is itself good news.");
        sb.AppendLine();

        foreach (var (name, definition) in MemoryAuditGlossary.All
                     .OrderBy(kv => kv.Value.Severity == MemoryAuditStatuses.Alert ? 0 : 1)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### `{name}` — {definition.Title}");
            sb.AppendLine($"*{definition.Severity}*");
            sb.AppendLine();
            sb.AppendLine(definition.WhatItMeans);
            sb.AppendLine();
            sb.AppendLine($"**What to do:** {definition.WhatToDo}");
            sb.AppendLine();
        }

        sb.AppendLine("## What the audit does not claim");
        sb.AppendLine();
        sb.AppendLine(
            "- It counts and checks structure; it cannot tell whether a memory is *true*.");
        sb.AppendLine(
            "- Its duplicate count is a lexical measure. Two memories that say the same thing in " +
            "completely different words are not counted as duplicates by it.");
        sb.AppendLine(
            "- A rate like \"entries per day\" needs a long enough gap between runs. After a " +
            "restart puts two runs close together, the rate is reported as not measurable rather " +
            "than as a misleading number.");
        sb.AppendLine(
            "- Memories deleted outright take their category with them, so a category that vanished " +
            "entirely shows up in the deletion count rather than in the per-category table.");

        return sb.ToString();
    }
}
