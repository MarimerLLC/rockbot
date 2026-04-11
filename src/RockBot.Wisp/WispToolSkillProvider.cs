using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Provides a skill guide for the wisp executor subsystem.
/// </summary>
public sealed class WispToolSkillProvider : IToolSkillProvider
{
    public string Name => "wisp";
    public string Summary => "Lightweight pipelines for procedural multi-step tasks (spawn_wisps). Run multiple wisps concurrently. Much cheaper than subagents for structured workflows.";

    public string GetDocument() =>
        """
        # Wisp Pipeline Guide

        A wisp is a **lightweight, harness-supervised pipeline** for procedural multi-step
        tasks. Unlike subagents (which receive full agent context on every LLM round-trip),
        wisps execute steps with minimal or zero LLM involvement — saving 80-95% of tokens
        for structured workflows.

        ## When to use wisps vs. subagents

        | Use a **wisp** when... | Use a **subagent** when... |
        |------------------------|---------------------------|
        | Steps are known in advance | Task requires discovery/improvisation |
        | Mostly tool calls with known parameters | Heavy judgment or multi-turn reasoning |
        | Data pipeline: fetch → transform → output | Open-ended research or analysis |
        | 2-12K tokens total | 60-80K tokens acceptable |

        ## spawn_wisps

        Execute one or more wisp pipelines. Multiple wisps run **concurrently** (up to the
        configured limit). Returns a batch result with per-wisp success/failure and writes
        a JSON summary to working memory for programmatic consumption.

        ### Definition format

        ```json
        {
          "definitions": [
            {
              "description": "Human-readable description of what this pipeline does",
              "tools": ["web_browse"],
              "steps": [
                { "id": "step1", "mode": "Direct", "gateway": "...", ... },
                { "id": "step2", "mode": "Llm", "prompt": "...", ... }
              ]
            }
          ]
        }
        ```

        A single wisp works fine — just pass a one-element array.

        ### Parallel execution

        When multiple definitions are provided, wisps execute concurrently:
        - Each wisp is fully independent — no cross-wisp template references
        - Failures in one wisp do NOT abort others
        - All wisps complete before results are returned
        - Concurrency is gated by the system (callers don't need to worry about limits)

        ### Batch result format

        ```
        3 wisp(s) completed (2 succeeded, 1 failed, 1.2s total):

        - `wisp-abc123`: "Fetch calendar events" [ok] (800ms)
          Output: ...
        - `wisp-def456`: "Search recent emails" [ok] (1.2s)
          Output: ...
        - `wisp-ghi789`: "Check project status" [failed] (600ms)
          Error (External): Service unavailable

        Batch ID: `batch-abc123def456ab`
        Batch summary: `wisp/batch-batch-abc123def456ab/summary`
        ```

        The batch summary is always written to working memory as JSON, enabling
        downstream steps to programmatically inspect results.

        ### Step modes

        - **`Direct`** — The harness calls the tool with exact parameters you provide.
          **Zero LLM tokens.** Use for all deterministic operations.
        - **`Llm`** — A lightweight LLM interprets your prompt and makes tool calls.
          **The wisp LLM has NO agent context** — no soul, no memory, no conversation
          history, no skill index, no directives. It receives only the wisp step prompt
          and any data injected via `input_from`. Your prompt must be completely
          self-contained: include all relevant context (names, dates, timezone, format
          requirements) directly in the prompt text. Use only when judgment is needed
          (selection, filtering, summarization).

        ### Gateways (for Direct steps)

        Each Direct step routes through a gateway that maps to the correct registered tool:

        **MCP** — Call any MCP server tool:
        ```json
        {
          "id": "get_emails",
          "mode": "Direct",
          "gateway": "Mcp",
          "server": "ms365",
          "tool": "search_emails",
          "params": { "query": "from:sales subject:report", "max_results": 5 },
          "output_to": "wisp-data/emails.json"
        }
        ```

        **Script** — Execute a Python script in an ephemeral container:
        ```json
        {
          "id": "parse",
          "mode": "Direct",
          "gateway": "Script",
          "params": {
            "script": "import json\nwith open('/rockbot/shared/wisp-data/emails.json') as f:\n    data = json.load(f)\nprint(json.dumps(data))",
            "pip_packages": ["pandas"],
            "timeout_seconds": 60
          }
        }
        ```

        **Web** — Web search or page browsing:
        ```json
        {
          "id": "search",
          "mode": "Direct",
          "gateway": "Web",
          "tool": "web_search",
          "params": { "query": "quarterly earnings report", "count": 5 }
        }
        ```

        **A2A** — Invoke an external agent:
        ```json
        {
          "id": "analyze",
          "mode": "Direct",
          "gateway": "A2A",
          "agent": "market-analyst",
          "skill": "competitive-analysis",
          "message": "Analyze the data at /rockbot/shared/wisp-data/merged.json",
          "timeout_minutes": 5
        }
        ```

        ### LLM steps

        LLM steps omit the gateway. The wisp LLM is a blank slate — it has no
        knowledge of the user, the agent's personality, prior conversations, or
        any context beyond what you put in the prompt and `input_from` data.
        Write prompts as if briefing a stranger who knows nothing about the situation:
        ```json
        {
          "id": "summarize",
          "mode": "Llm",
          "prompt": "Summarize the key trends and highlight outliers",
          "input_from": "wisp-data/parsed.json"
        }
        ```

        The LLM step's tool scope is automatically built from:
        - All tools implied by Direct steps' gateway declarations
        - Tools listed in the top-level `tools` array
        - Working memory tools (always available)

        ### Data flow between steps

        **`output_to`** — Write step results to a file on the shared volume:
        ```json
        { "id": "fetch", ..., "output_to": "wisp-data/results.json" }
        ```

        **`input_from`** — Read from a file or prior step result:
        ```json
        { "id": "process", ..., "input_from": "wisp-data/results.json" }
        ```

        **Template substitution** — Reference prior step results in params:
        ```json
        { "params": { "data": "{{steps.fetch.result}}" } }
        { "message": "Process file at {{steps.fetch.output_to}}" }
        ```

        **Important:** Template references (`{{steps.id.result}}`) work within a single
        wisp's steps. Cross-wisp references are not supported — wisps in a batch are
        fully independent.

        **Transition patterns:**
        - `Direct → Direct`: Data passes via files on the shared volume
        - `Direct → Llm`: Harness reads file, chunks into working memory if large
        - `Llm → Direct`: Harness writes LLM output to shared volume
        - `Llm → Llm`: Data stays in working memory, no file round-trip

        ### Error handling

        **`on_failure`** — Define fallback behavior for Direct steps:
        ```json
        {
          "id": "risky",
          ...,
          "on_failure": { "action": "skip_to", "skip_to": "fallback_step" }
        }
        ```
        Default is `"abort"` — the pipeline stops and returns the error.

        In a batch, a failed wisp does NOT affect other wisps. All wisps run to
        completion regardless of individual failures.

        ### Failure classification

        Every failure is automatically classified:
        - **Structural** — Wrong tool name, missing params, bad gateway config (fix your definition)
        - **External** — Timeout, service unavailable, rate limit (transient, retry may help)
        - **Data** — Unexpected format, empty results, schema mismatch (fix assumptions)
        - **Judgment** — LLM step picked wrong result or missed data (refine the prompt)

        ### Best practices

        1. **Maximize Direct steps.** Every Direct step costs zero LLM tokens.
        2. **Use LLM steps only for judgment.** Selection, filtering, summarization,
           interpretation — anything that requires understanding content.
        3. **Use output_to/input_from for cross-step data.** Don't try to pass large
           data through template substitution.
        4. **Scripts can read/write the shared volume directly.** Use file paths like
           `/rockbot/shared/wisp-data/file.json` in your Python scripts.
        5. **Keep descriptions specific.** The description is used for retry detection
           and failure pattern analysis across sessions.
        6. **Batch independent work.** When you need multiple unrelated data fetches,
           put them in a single `spawn_wisps` call to run concurrently.

        ### Example: Parallel data gathering

        ```json
        {
          "definitions": [
            {
              "description": "Fetch calendar events for today",
              "steps": [
                {
                  "id": "get_events",
                  "mode": "Direct",
                  "gateway": "Mcp",
                  "server": "google-calendar",
                  "tool": "gcal_list_events",
                  "params": { "time_min": "2024-01-15T00:00:00Z", "time_max": "2024-01-15T23:59:59Z" },
                  "output_to": "wisp-data/calendar.json"
                }
              ]
            },
            {
              "description": "Search recent emails from team",
              "steps": [
                {
                  "id": "search",
                  "mode": "Direct",
                  "gateway": "Mcp",
                  "server": "ms365",
                  "tool": "outlook_email_search",
                  "params": { "query": "from:team newer:1d", "max_results": 10 },
                  "output_to": "wisp-data/emails.json"
                }
              ]
            },
            {
              "description": "Check project build status",
              "steps": [
                {
                  "id": "status",
                  "mode": "Direct",
                  "gateway": "Web",
                  "tool": "web_browse",
                  "params": { "url": "https://ci.example.com/api/status" }
                }
              ]
            }
          ]
        }
        ```

        All three wisps execute concurrently. Total wall-clock time equals the
        slowest wisp, not the sum. Each costs zero LLM tokens (all Direct steps).
        """;
}
