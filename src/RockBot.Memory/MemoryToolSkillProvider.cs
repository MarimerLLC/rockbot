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
        - `query` (string, optional) — keyword search across content. Omit to browse.
        - `category` (string, optional) — category prefix filter (e.g. `user-preferences`).
          Prefix match: `project-context` also matches `project-context/rockbot`.
        - `mode` (string, optional) — `hybrid` (default) or `regex` for literal tokens

        ```
        search_memory(query: "timezone", category: "user-preferences")
        search_memory(category: "user-preferences")   # browse a topic area
        search_memory()                               # browse + see the category taxonomy
        ```

        **Browsing and the category taxonomy.** Call `search_memory` with no `query` to
        browse: you get the most recently reinforced entries in scope, followed by the
        full list of categories that exist. There is no separate list-categories tool —
        that taxonomy is this facet. Use it to discover how knowledge is organized, then
        re-search with a `category` filter to narrow.

        **Tips**
        - Search is automatically run against each incoming message — you usually don't
          need to search manually unless looking for something specific mid-task
        - Use `category` alone (no query) to browse all entries in a topic area
        - IDs appear in brackets in results: `[abc123]` — you need the ID to delete an entry


        ### Editing existing content: edit, don't rewrite

        `save_memory` creates new entries; it never amends one. `save_to_working_memory`
        replaces the entire cached value. Anything you do not reproduce in full is gone —
        and on a long entry you will not reliably reproduce it in full.

        So when the content already exists and you are changing part of it, use `edit_memory`
        or `edit_working_memory`. Reserve the save tools for new content and for deliberately
        replacing a whole short payload.

        For long-term memory this matters beyond the text. Every entry carries how long the
        fact has been known and how many times it has been reinforced, and search ranks and
        renders entries with it (`seen 12× from 2025-11 to today`). Delete-then-save throws all
        of that away and leaves a fact that has been seen once — so the fix for a wrong detail
        costs the agent everything it knew about how well-established the fact was.

        ### edit_memory

        Replaces an exact piece of text inside an existing entry's content, leaving the rest
        of the entry — and its id, age, and reinforcement count — untouched.

        **Parameters**
        - `id` (string, required) — the ID from search results (e.g. `abc123`)
        - `old_string` (string, required) — exact text to find in the entry's content
        - `new_string` (string, required) — replacement text; empty string deletes the match
        - `replace_all` (boolean, optional, default false) — change every occurrence

        ```
        edit_memory(
          id: "abc123",
          old_string: "prefers meetings in the morning",
          new_string: "prefers meetings after 13:00"
        )
        ```

        Rules:
        - `old_string` must match the stored content **exactly**. Search first and copy the
          text verbatim rather than reconstructing it.
        - `old_string` must match **exactly once**, or the edit is refused — include more
          surrounding text, or pass `replace_all: true`.
        - A refused edit is information, not an obstacle. "Not found" means your `old_string`
          does not match; re-read the entry rather than retrying the same text. Do not fall
          back to delete + save to work around a refusal — that is the loss this tool avoids.


        ### delete_memory

        Deletes a memory entry by its ID. Use this when the entry is wrong or obsolete *as a
        whole* — a fact that turned out to be false, a duplicate, something the user asked you
        to forget. When the entry is mostly right and one detail changed, use `edit_memory`
        instead.

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
        outputs, patrol findings) using `search_working_memory(namespace: ...)`. Entries expire
        automatically based on their TTL (default: 5 minutes).

        ### Namespaces auto-injected into your context

        At the start of every turn the framework lists entry keys from these namespaces in
        your system context — you do not need to call `search_working_memory` to see them:

        - **Your own namespace** — always.
        - **`patrol/`** — in user sessions only; lets you see patrol findings.
        - **`subagent/*-index`** — in user sessions only; lets you see subagent research indexes.
        - **`shared/`** — in every context (user, patrol, subagent). This is the conventional
          cross-session handoff namespace. If you need to leave something short-lived for
          another session to pick up, write a full-path key beginning with `shared/`
          (e.g. `save_to_working_memory(key: "shared/drafts/tina-vslive-2026-04-17", data: ...)`).
          Other sessions will see the key automatically and can fetch the value with
          `get_from_working_memory`. Choose self-describing keys — discovery is by name, not
          content. Use `shared/` for pointers to files on the shared volume, not for large
          payloads that would be lost if the pod restarts.

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

        **Index chunks for large results:** When tool results or web pages are chunked,
        an index chunk (key ending in `-index`) is stored alongside the content chunks.
        It contains a hierarchical document outline mapping section headings to chunk keys.
        If you need to navigate chunked content but the inline index has scrolled out of
        context, retrieve the `-index` key first to rediscover the document structure.

        **Scanning all chunks: prefer parallel wisps over sequential reads.** When you need
        to search, filter, or extract information across every chunk (not jump to one known
        section), reading each chunk into your own context via `get_from_working_memory`
        scales the agent-context cost with total document size. Instead, fan out with
        `spawn_wisps` — one wisp per chunk, each with a single `Llm` step that calls
        `get_from_working_memory` on its assigned full-path chunk key and returns a compact
        JSON extract (e.g. matched sentences, pulled entities). The wisp LLM has a fresh
        context per chunk, so only the extracted results come back to your turn — not the
        full chunk content. This is the right pattern for "find everywhere X is mentioned,"
        "aggregate all entities of type Y," or "summarise each section" across a chunked
        document.

        Minimal per-chunk wisp step:
        ```json
        {
          "id": "scan",
          "mode": "Llm",
          "prompt": "Call get_from_working_memory with key 'session/abc123/research-chunk3'. From that content, extract every sentence mentioning 'Northstar'. Return ONLY a JSON array of strings ([] if none)."
        }
        ```
        Spawn N of these — one per chunk key from the `-index` — in a single `spawn_wisps`
        call. See the wisp guide for full pipeline mechanics.


        ### edit_working_memory

        Replaces an exact piece of text inside a cached entry, leaving the rest of the value
        untouched. Use it to amend a running draft, checklist, or handoff note in place instead
        of re-sending the whole payload through `save_to_working_memory`.

        **Parameters**
        - `key` (string, required) — plain key for your own namespace, or a full path
        - `old_string` (string, required) — exact text to find in the cached value
        - `new_string` (string, required) — replacement text; empty string deletes the match
        - `replace_all` (boolean, optional, default false) — change every occurrence

        ```
        edit_working_memory(
          key: "shared/drafts/tina-vslive",
          old_string: "Session length: 60 minutes",
          new_string: "Session length: 75 minutes"
        )
        ```

        The entry keeps its category and tags, and its TTL restarts from now using the same
        window it was stored with — an entry you are still amending is one still in use. Same
        exact-match rules as `edit_memory`: verbatim `old_string`, one match unless
        `replace_all`.


        ### search_working_memory

        Searches *and* lists cached entries. Defaults to your own namespace; pass `namespace`
        to browse or search another context. There is no separate list tool — omitting `query`
        is the listing.

        **Parameters**
        - `query` (string, optional) — keywords to search for in cached content. Omit to list.
        - `category` (string, optional) — category prefix filter
        - `tags` (string, optional) — comma-separated tags that entries must have
        - `namespace` (string, optional) — namespace to search (e.g. `subagent/task1`, `patrol`)

        **Two modes, chosen by whether you pass `query`:**

        - **With `query`** — entries are ranked by relevance and each line ends with a
          120-character content preview.
        - **Without `query`** — every entry in scope is listed with key, category, tags, and
          expiry, and no content preview. This is the compact browse surface.

        ```
        search_working_memory()                                     # list own namespace
        search_working_memory(namespace: "subagent/task-abc123")    # list subagent outputs
        search_working_memory(namespace: "patrol")                  # list all patrol outputs
        search_working_memory(query: "unread emails", category: "email")
        search_working_memory(query: "findings", namespace: "patrol/heartbeat")
        ```

        The system also shows a working memory summary in your context at the start of each
        turn — check that first before listing.


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
        - **Correct wrong facts promptly** — stale or incorrect long-term memories can
          silently degrade future responses. Fix the detail with `edit_memory`; reserve
          `delete_memory` for entries that are wrong as a whole


        ## Common Pitfalls

        - Saving raw tool output to long-term memory — it's too noisy; save a summarized
          fact instead, or use working memory if the raw data is needed short-term
        - Forgetting that `save_memory` returns immediately — the actual save happens
          in the background; don't assume it's instantly searchable
        - Confusing working memory scope — your writes go to your namespace only; cross-context
          reads require explicit `namespace` parameter or a full path key
        - Ignoring the working memory context shown at the start of each turn — always
          check it before calling `search_working_memory`
        - Reaching for the wrong search: `search_memory` is durable cross-session knowledge,
          `search_working_memory` is this-session cached payloads. If the thing you want was
          fetched or produced during this conversation, it is in working memory.
        """;
}
