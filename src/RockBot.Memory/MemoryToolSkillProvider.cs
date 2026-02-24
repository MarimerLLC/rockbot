using RockBot.Tools;

namespace RockBot.Memory;

/// <summary>
/// Provides the agent with a usage guide for all three memory tiers:
/// long-term memory, working memory, and behavioral rules.
/// Registered automatically when <c>WithMemory()</c> is called.
/// </summary>
public sealed class MemoryToolSkillProvider : IToolSkillProvider
{
    public string Name => "memory";
    public string Summary => "Long-term memory, working memory (path-namespaced, cross-context), and behavioral rules — when and how to use each.";

    public string GetDocument() =>
        """
        # Memory Systems Guide

        Three complementary memory systems let the agent persist knowledge and shape
        its behavior over time. Knowing which to use — and when — is essential.

        | System | Scope | Purpose |
        |---|---|---|
        | Long-term memory | Permanent, cross-session | Facts, preferences, learned patterns |
        | Working memory | Global, path-namespaced, TTL-based | Tool results, intermediate data, cross-context handoff |
        | Rules | Permanent, injected every turn | Hard behavioral constraints |


        ## Long-Term Memory

        Long-term memory stores facts that should be recalled in future sessions —
        user preferences, domain knowledge, learned patterns, and anything that would
        be useful to remember days or weeks from now.

        ### When to save

        - User shares a preference ("I prefer concise answers", "my timezone is US/Central")
        - You learn something specific about the user's domain or context
        - A fact arises that would help you give better answers in future sessions
        - The user explicitly asks you to remember something

        ### When NOT to save

        - Temporary data relevant only to this conversation (use working memory instead)
        - Raw tool output or large payloads (too noisy; summarize if worth keeping)
        - Anything the user hasn't indicated should persist


        ### save_memory

        Saves a fact to long-term memory. The content is enriched and split into focused
        entries by a background LLM call — you don't need to pre-structure it. Returns
        immediately with "Memory save queued."

        **Parameters**
        - `content` (string, required) — natural-language description of the fact to remember
        - `category` (string, optional) — hierarchical category hint (e.g. `user-preferences/communication`)
        - `tags` (string, optional) — comma-separated tag hints (e.g. `"timezone,scheduling"`)

        ```
        save_memory(
          content: "User prefers responses in bullet points rather than prose",
          category: "user-preferences/style",
          tags: "formatting"
        )
        ```

        **Tips**
        - Write content as a complete sentence — the enrichment process works best with
          natural language, not terse notes
        - Categories use slash-separated hierarchy: `user-preferences/communication`,
          `project/rockbot`, `domain/finance`
        - Split compound facts into separate `save_memory` calls when they cover
          different topics — the enrichment will also do this automatically


        ### search_memory

        Searches long-term memory by keyword and/or category. Results are returned with
        IDs, categories, tags, and age.

        **Parameters**
        - `query` (string, optional) — keyword search across content
        - `category` (string, optional) — category prefix filter (e.g. `user-preferences`)

        ```
        search_memory(query: "timezone", category: "user-preferences")
        ```

        **Tips**
        - Search is automatically run against each incoming message — you usually don't
          need to search manually unless looking for something specific mid-task
        - Use `category` alone (no query) to browse all entries in a topic area
        - IDs appear in brackets in results: `[abc123]` — you need the ID to delete an entry


        ### list_categories

        Lists all existing category prefixes so you can understand how memory is organized
        before searching.

        ```
        list_categories()
        ```


        ### delete_memory

        Deletes a memory entry by its ID. Use this to remove entries that are wrong,
        outdated, or superseded. To correct a fact: delete the old entry, then save the
        corrected version.

        **Parameters**
        - `id` (string, required) — the ID from search results (e.g. `abc123`)

        ```
        delete_memory(id: "abc123")
        ```


        ---

        ## Working Memory

        Working memory is a global, path-namespaced scratch space shared by all execution
        contexts — user sessions, patrol tasks, and subagents. Your entries are automatically
        stored under your session namespace. You can read from other namespaces (e.g. subagent
        outputs, patrol findings) using `list_working_memory(namespace: ...)`. Entries expire
        automatically based on their TTL (default: 5 minutes).

        ### When to save

        - After receiving a large payload from any tool (email list, calendar events,
          file contents, search results) that the user is likely to ask follow-up questions about
        - When a tool result took significant time or tokens to fetch and might be needed again

        ### When NOT to save

        - Small or simple results that are cheap to re-fetch
        - Data that will definitely not be referenced again in this session
        - Facts worth keeping long-term (use long-term memory instead)


        ### save_to_working_memory

        Caches data under a descriptive key with an optional TTL. The key is automatically
        stored under your session namespace — plain keys like `emails_inbox` are sufficient.

        **Parameters**
        - `key` (string, required) — short descriptive key (e.g. `emails_inbox_2026-02-19`)
        - `data` (string, required) — the content to cache; can be large JSON, formatted text, etc.
        - `ttl_minutes` (integer, optional, default 5) — how long to keep this entry. Use longer TTLs for subagent or patrol outputs (e.g. 240).
        - `category` (string, optional) — groups related entries (e.g. `email`, `calendar`)
        - `tags` (string, optional) — comma-separated tags for filtering

        ```
        save_to_working_memory(
          key: "inbox_emails_2026-02-19",
          data: "<raw email list JSON>",
          ttl_minutes: 15,
          category: "email",
          tags: "inbox,unread"
        )
        ```

        **Tips**
        - Choose keys that describe the content and timestamp so they're unambiguous
        - Set a longer TTL for data that may be referenced across many follow-up turns
        - Always add a category — it makes `search_working_memory` much more effective


        ### get_from_working_memory

        Retrieves a cached entry by key. Use a plain key (e.g. `emails_inbox`) to read from
        your own namespace, or a full path (e.g. `subagent/task1/results`) to read across
        namespaces.

        **Parameters**
        - `key` (string, required) — plain key for own namespace, or full path for cross-namespace access

        ```
        get_from_working_memory(key: "inbox_emails_2026-02-19")
        ```


        ### list_working_memory

        Lists all active entries in the given namespace. Defaults to your own session namespace.
        Pass a `namespace` parameter to browse other contexts (subagents, patrol tasks).
        The system also shows a working memory summary in your context at the start
        of each turn — check that first before calling this tool.

        **Parameters**
        - `namespace` (string, optional) — namespace to list (e.g. `subagent/task1`, `patrol`). Omit to list your own.

        ```
        list_working_memory()                                   # own namespace
        list_working_memory(namespace: "subagent/task-abc123")  # subagent outputs
        list_working_memory(namespace: "patrol")                # all patrol outputs
        ```


        ### search_working_memory

        Searches cached entries by keyword, category, and/or tags using BM25 relevance ranking.
        Defaults to your own namespace. Pass `namespace` to search another context.

        **Parameters**
        - `query` (string, optional) — keywords to search for in cached content
        - `category` (string, optional) — category prefix filter
        - `tags` (string, optional) — comma-separated tags that entries must have
        - `namespace` (string, optional) — namespace to search (e.g. `subagent/task1`, `patrol`)

        ```
        search_working_memory(query: "unread emails", category: "email")
        search_working_memory(query: "findings", namespace: "patrol/heartbeat")
        ```


        ---

        ## Best Practices

        - **Prefer working memory for large payloads** — long-term memory is for facts,
          not raw data dumps
        - **Search long-term memory before asking the user** — if a preference or fact
          might already be remembered, check first
        - **Set realistic TTLs** — 5 minutes suits quick follow-ups; 15–30 minutes for
          research sessions; keep it short to avoid stale data
        - **Use consistent category conventions** — `user-preferences/*`, `project/*`,
          `domain/*` for long-term; `email`, `calendar`, `research` for working memory
        - **Delete wrong facts promptly** — stale or incorrect long-term memories
          can silently degrade future responses


        ## Common Pitfalls

        - Saving raw tool output to long-term memory — it's too noisy; save a summarized
          fact instead, or use working memory if the raw data is needed short-term
        - Forgetting that `save_memory` returns immediately — the actual save happens
          in the background; don't assume it's instantly searchable
        - Confusing working memory scope — your writes go to your namespace only; cross-context
          reads require explicit `namespace` parameter or a full path key
        - Ignoring the working memory context shown at the start of each turn — always
          check it before calling `list_working_memory`
        """;
}
