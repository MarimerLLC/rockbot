using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Provides a skill guide for the wisp executor subsystem.
/// </summary>
public sealed class WispToolSkillProvider : IToolSkillProvider
{
    public string Name => "wisp";
    public string Summary => "Lightweight pipelines for procedural multi-step tasks (spawn_wisp). Much cheaper than subagents for structured workflows.";

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

        ## spawn_wisp

        Execute a wisp pipeline synchronously. Returns structured results with per-step
        success/failure and error classification.

        ### Definition format

        ```json
        {
          "definition": {
            "description": "Human-readable description of what this pipeline does",
            "tools": ["web_browse"],
            "steps": [
              { "id": "step1", "mode": "Direct", "gateway": "...", ... },
              { "id": "step2", "mode": "Llm", "prompt": "...", ... }
            ]
          }
        }
        ```

        ### Step modes

        - **`Direct`** — The harness calls the tool with exact parameters you provide.
          **Zero LLM tokens.** Use for all deterministic operations.
        - **`Llm`** — A lightweight LLM interprets your prompt and makes tool calls
          with minimal context. Use only when judgment is needed (selection, filtering,
          summarization).

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
            "script": "import json\\nwith open('/rockbot/shared/wisp-data/emails.json') as f:\\n    data = json.load(f)\\nprint(json.dumps(data))",
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

        LLM steps omit the gateway. They receive a prompt and can use tools from
        the pipeline's scope:
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

        ### Failure classification

        Every failure is automatically classified:
        - **Structural** — Wrong tool name, missing params, bad gateway config (fix your definition)
        - **External** — Timeout, service unavailable, rate limit (transient, retry may help)
        - **Data** — Unexpected format, empty results, schema mismatch (fix assumptions)
        - **Judgment** — LLM step picked wrong result or missed data (refine the prompt)

        ### Result format

        On success:
        ```
        Wisp `wisp-abc123` completed successfully (3 steps, 450ms).
        - step1 [ok] (120ms) — Output: ...
        - step2 [ok] (200ms) — Output: ...
        - step3 [ok] (130ms) — Output: ...
        Working memory namespace: `wisp/wisp-abc123`
        ```

        On failure:
        ```
        Wisp `wisp-abc123` failed at step `parse` (index 1).
        Error category: Data
        Error: Unexpected file format
        ```

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

        ### Example: Email search and summary pipeline

        ```json
        {
          "definition": {
            "description": "Search recent emails from sales team and summarize action items",
            "steps": [
              {
                "id": "search",
                "mode": "Direct",
                "gateway": "Mcp",
                "server": "ms365",
                "tool": "outlook_email_search",
                "params": { "query": "from:sales newer:2d", "max_results": 10 },
                "output_to": "wisp-data/sales-emails.json"
              },
              {
                "id": "summarize",
                "mode": "Llm",
                "prompt": "Extract action items from these emails. List each with owner, deadline, and priority.",
                "input_from": "wisp-data/sales-emails.json"
              }
            ]
          }
        }
        ```
        Step 1 costs zero LLM tokens. Step 2 uses a lightweight LLM with only the
        email data in context — no soul, no memory recall, no skill index.
        """;
}
