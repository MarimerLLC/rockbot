namespace RockBot.Host;

/// <summary>
/// Working-memory read tools whose results are <i>explicit</i> retrievals of content the
/// agent already chose to load (or enumerate). Their results must never be re-chunked,
/// re-capped, or re-stashed.
///
/// <para><b>Why.</b> The chunk/cap/stash machinery replaces an oversized tool result with
/// a head + elision marker + tail surface and parks the full original in working memory
/// under a key derived from the <i>call id</i>, telling the model to fetch it via
/// <c>GetFromWorkingMemory</c>. If that retrieval result is itself oversized and gets
/// re-stashed, it lands under the retrieval call's <i>new</i> id — so the model, dutifully
/// fetching the newly-advertised key, retrieves a slightly larger reference, which is
/// re-stashed under yet another id, and so on. The result is a retrieve→re-stash→retrieve
/// loop that makes no progress until the surrounding iteration/timeout budget kills it.
/// (Observed 2026-06-10: a communications-briefing subagent burned its full 15-minute
/// budget in exactly this loop after pulling a ~15k-char multi-account email payload.)</para>
///
/// <para>Exempting these tools means an explicit retrieval is honoured in full and left
/// alone — matching the long-standing chunking exemption these same tools already had.</para>
/// </summary>
internal static class StashExemptTools
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_from_working_memory",
        // Covers both the ranked-search and the query-less listing path, which absorbed the
        // former ListWorkingMemory tool.
        "search_working_memory",

        // The PascalCase method names these tools were registered under before issue #493
        // pinned them to snake_case. Kept deliberately: this set is the guard against a
        // non-terminating retrieve→re-stash loop (see above), so a stale name arriving from
        // an in-flight request during a rolling deploy is the wrong place to be clever.
        "GetFromWorkingMemory",
        "SearchWorkingMemory",
    };

    /// <summary>
    /// True when <paramref name="toolName"/> is an explicit working-memory read whose
    /// result must not be re-chunked, re-capped, or re-stashed.
    /// </summary>
    public static bool Contains(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && Names.Contains(toolName);
}
