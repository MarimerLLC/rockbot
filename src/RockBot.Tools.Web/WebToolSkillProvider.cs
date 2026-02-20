using RockBot.Tools;

namespace RockBot.Tools.Web;

/// <summary>
/// Provides the agent with a usage guide for the web search and browse tools.
/// Registered automatically when <c>AddWebTools()</c> is called.
/// </summary>
internal sealed class WebToolSkillProvider : IToolSkillProvider
{
    public string Name => "web";
    public string Summary => "Web search and page browsing tools (web_search, web_browse).";

    public string GetDocument() =>
        """
        # Web Tools Guide

        Two tools provide access to live web content: `web_search` for finding relevant
        pages and `web_browse` for reading them in full.


        ## When to Use This Guide

        Consult this guide when you need current information, external facts, documentation,
        or any content that isn't in your training data or conversation context.


        ## Step 0 — Check for an Existing Skill First

        Before searching, check whether a skill already documents reliable sources or a
        research pattern for this topic. Past research may have identified the best sites
        to consult, saving redundant searches.

        ```
        list_skills()
        ```

        Look for skills named `web/{topic}` (e.g. `web/dotnet-docs`, `web/news-sources`).
        If one exists, load it with `get_skill` and use the sources it recommends directly
        with `web_browse` — skip the search step.


        ## Step 1 — Formulate a Good Search Query

        Query quality determines result quality. Before calling `web_search`:

        - Use specific, keyword-rich terms rather than natural-language questions
        - Include version numbers, product names, or proper nouns when relevant
        - Add a site filter for known authoritative sources when appropriate

        | Instead of | Use |
        |---|---|
        | "how do I use async in dotnet" | "C# async await best practices .NET 10" |
        | "what happened in the news today" | "technology news February 2026" |
        | "python install instructions" | "Python 3.13 install Windows official docs" |


        ## Step 2 — Search the Web

        **Parameters**
        - `query` (string, required) — the search query
        - `count` (integer, optional, 1–20, default 10) — number of results to return

        ```
        web_search(query: "C# async await best practices .NET 10", count: 5)
        ```

        Evaluate the results:
        - Read titles and snippets to identify the most authoritative and relevant pages
        - Prefer official documentation, reputable publications, and primary sources
        - Use `count: 3–5` for quick factual lookups; higher counts for broad research
        - If no results look useful, refine the query and search again before browsing


        ## Step 3 — Browse for Full Content (when needed)

        Search snippets are often sufficient for simple factual questions. Only call
        `web_browse` when you need the complete page content.

        **Parameters**
        - `url` (string, required) — the full URL of the page to fetch

        ```
        web_browse(url: "https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/")
        ```

        - Browse the highest-confidence URL from Step 2 first
        - You can browse a URL directly without searching first when you already know
          the authoritative source (e.g. official docs, GitHub releases page)

        ### Large Pages — Automatic Chunking

        When a page is large, `web_browse` automatically splits it into chunks and saves
        them to working memory. Instead of returning all the content at once, it returns
        a **chunk index** — a table listing each chunk's heading and key:

        ```
        | # | Heading          | Key                              |
        |---|------------------|----------------------------------|
        | 0 | Introduction     | `web:learn.microsoft.com_...:chunk0` |
        | 1 | Getting Started  | `web:learn.microsoft.com_...:chunk1` |
        | 2 | API Reference    | `web:learn.microsoft.com_...:chunk2` |
        ```

        To read a chunk, call:
        ```
        GetFromWorkingMemory(key: "web:learn.microsoft.com_...:chunk1")
        ```

        - Only load the chunks you actually need — read headings to pick the relevant ones
        - Use `ListWorkingMemory()` to see all cached chunks and their expiry times
        - Chunks expire after 20 minutes; re-browse the page if they are gone


        ## Step 4 — Synthesize and Report

        - Summarize the relevant findings rather than quoting large blocks of raw content
        - Cite the source URL so the user can verify or read further
        - Note the publication or last-updated date for time-sensitive information
        - Distinguish between what you found and what you inferred from it


        ## Step 5 — Save Useful Sources as a Skill

        When research reveals consistently reliable sources for a topic — or when a
        multi-step research workflow is worth repeating — save it as a skill.

        Call `save_skill` with:
        - **name**: `web/{topic}` in lowercase with hyphens (e.g. `web/dotnet-docs`,
          `web/azure-pricing`, `web/local-news`)
        - **content**: a markdown document covering:
          - What this skill covers and when to use it
          - The best URLs or search queries to start with
          - Any site-specific quirks (pagination, truncation, login walls)
          - Example queries that produced good results

        Future tasks on the same topic will find this skill in Step 0 and go straight
        to the authoritative sources without searching first.


        ## Tool Reference

        ### web_search
        Search the web and receive titles, URLs, and snippets for matching pages.
        Use this to discover relevant URLs before deciding what to browse.

        ### web_browse
        Fetch a web page and receive its full content as Markdown.
        Use this when you need more than the snippet — full articles, documentation,
        release notes, or structured data.


        ## Best Practices

        - **Search before browsing** — use snippets to pick the best URL; don't browse
          speculatively
        - **Prefer primary sources** — official docs, release pages, and canonical
          references over aggregators and summaries
        - **Large pages are chunked** — if `web_browse` returns a chunk index, use
          `GetFromWorkingMemory` to load only the sections you need
        - **Don't over-search** — two to three targeted searches are usually better than
          ten vague ones


        ## Common Pitfalls

        - Vague queries that return off-topic results — be specific
        - Browsing every search result instead of evaluating snippets first
        - Treating a snippet as the full answer when the detail is in the page body
        - Ignoring the chunk index and reporting incomplete content as complete
        - Not citing sources, making it hard for the user to verify findings
        """;
}
