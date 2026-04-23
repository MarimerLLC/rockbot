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

        A wisp is a **deterministic pipeline** — a static step graph the harness executes
        top-down. No loops. No runtime branching on discovered data. No cross-wisp state.
        If your workflow requires iterating over items that aren't known when you write
        the definition, the iteration happens in the *parent agent* — by calling
        `spawn_wisps` multiple times — not inside a single wisp.

        Unlike subagents (which receive full agent context on every LLM round-trip),
        wisps execute steps with minimal or zero LLM involvement — saving 80-95% of tokens
        for structured workflows.

        **Shape check before authoring:**

        | Good wisp | Bad wisp |
        |-----------|----------|
        | `fetch_event → transform → write_file → send_email` | `discover_accounts → for-each-account: discover_calendars → for-each-calendar: fetch_events` |
        | Every step's inputs are known (or come from a prior step) | Step count depends on runtime data |
        | Fan-out is over a list you already have | Fan-out requires looping over a discovered list |

        If the shape on the right is what you need, the iteration lives in the parent agent
        (see "When a single wisp is not enough" below).

        ## When to use wisps vs. subagents

        | Use a **wisp** when... | Use a **subagent** when... |
        |------------------------|---------------------------|
        | Steps are known in advance | Task requires discovery/improvisation |
        | Mostly tool calls with known parameters | Heavy judgment or multi-turn reasoning |
        | Data pipeline: fetch → transform → output | Open-ended research or analysis |
        | 2-12K tokens total | 60-80K tokens acceptable |

        **Wisps are a bad fit when:**

        - The number of steps isn't known until something earlier runs.
        - A loop is required (for-each over a discovered list, while-condition, retry-until).
        - Results of one step determine how many downstream steps there should be.
        - Complex conditional branching is needed (if/else trees beyond simple `on_failure`).
        - Heavy reasoning or multi-turn deliberation is required inside the pipeline.

        In those cases, either the **parent agent** does the iteration (spawning additional
        wisp batches — see next section), or the whole task belongs in a **subagent** that
        can itself spawn wisps for the procedural sub-steps.

        ## When a single wisp is not enough

        A wisp has a static step graph — it cannot loop over results or generate new steps
        from runtime data. When you need per-item processing of items you don't know in
        advance, iterate at the agent level by calling `spawn_wisps` more than once.

        **Static fan-out** — you know the N items up front (e.g. a known list of accounts
        to query). Put N definitions in a single `spawn_wisps` call; each has a literal
        per-item `params`:

        ```json
        {
          "definitions": [
            { "description": "Events for marimer-work",
              "steps": [{ "id": "e", "mode": "Direct", "gateway": "Mcp",
                          "server": "calendar-mcp", "tool": "get_calendar_events",
                          "params": { "accountId": "marimer-work", "timeZone": "America/Chicago",
                                      "startDate": "2026-04-23", "endDate": "2026-04-23" }}]
            },
            { "description": "Events for xebia",
              "steps": [{ "id": "e", "mode": "Direct", "gateway": "Mcp",
                          "server": "calendar-mcp", "tool": "get_calendar_events",
                          "params": { "accountId": "xebia", "timeZone": "America/Chicago",
                                      "startDate": "2026-04-23", "endDate": "2026-04-23" }}]
            }
          ]
        }
        ```

        **Dynamic fan-out** — you discover the N items at runtime:

        1. Discover. Either a direct tool call (`mcp_invoke_tool(list_accounts)`) or a
           small discovery wisp that returns the list.
        2. Read the result in the parent agent.
        3. Compose a second `spawn_wisps` call with N definitions, one per discovered
           item. Each has a literal `params` — no template can insert a per-account
           value into a fixed-shape step graph.

        Do **not** try to write a single wisp of the form
        `list_accounts → [something that loops] → get_calendar_events`. There is no "for
        each" step. The outer iteration belongs in the parent agent.

        **Multi-stage fan-out — a worked example.** Suppose the user asks "show me every
        meeting tomorrow across all my accounts." The enumeration shape is
        `accounts → calendars per account → events per calendar`. Each stage's count
        depends on the previous stage's data, so no single wisp can express it. The
        parent agent runs it as a sequence of batches:

        1. **Batch 1 — discover accounts.** One wisp (or a direct `mcp_invoke_tool` call):
           `list_accounts` → returns `["marimer-work", "xebia", "personal"]`.
        2. **Read** the result in the parent agent.
        3. **Batch 2 — parallel per account.** One `spawn_wisps` call with N=3 definitions,
           each calling `list_calendars` with a literal `accountId`. They run concurrently.
        4. **Read** the batch summary; collect the (accountId, calendarId) pairs.
        5. **Batch 3 — parallel per calendar.** One `spawn_wisps` call with one definition
           per (accountId, calendarId) pair, each calling `get_calendar_events` with literal
           params. Again concurrent.
        6. **Aggregate** the results in the parent agent and answer the user.

        Every wisp in every batch is static and deterministic. The dynamism lives in the
        agent's decision to build the next batch from the previous batch's output. This
        is much cheaper than a subagent — each per-item wisp costs zero LLM tokens — but
        it does require the parent agent to hold state between batches.

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

        Each Direct step routes through a gateway that maps to the correct registered tool.

        **IMPORTANT:** Tool arguments go in the `"params"` field (not `"input"` or
        `"arguments"`). Include ALL required parameters — the harness passes them
        directly to the tool with no defaulting or inference.

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

        Two template forms are supported:

        - `{{steps.<id>.result}}` — inlines the upstream step's entire output string.
          Use when the downstream consumer wants the whole blob.
        - `{{steps.<id>.result.a.b.c}}` — parses the upstream output as JSON and inserts
          the value at the dotted path. Strings are unwrapped; objects and arrays become
          their JSON representation.

        **Constraints on field-path templates — all of these are enforced:**

        - Upstream content must parse as a **JSON object**. Plain text, arrays at the root,
          or invalid JSON all cause the template to fall back to the literal.
        - **No array indexing.** There is no `[0]` syntax. If the upstream output is an
          array, add an `Llm` step between that flattens the element you want into a
          top-level object (e.g. `{ "eventId": "...", "accountId": "..." }`), then
          field-path the next step's params from that.
        - **Silent fallback** — if the upstream isn't JSON or the path doesn't resolve,
          the literal `{{steps.id.result.path}}` is passed to the tool unchanged. The
          downstream call will then fail visibly (soft-error detection catches these).
        - **Single-wisp scope.** Templates resolve within one wisp only. Cross-wisp
          references are not supported — wisps in a batch are fully independent.
        - **Static resolution.** You cannot use substitution to generate new steps — the
          step graph is fixed at submission time. Templates only fill in string values
          inside an existing step's params/message/input_from.

        **Transition patterns:**
        - `Direct → Direct`: Data passes via files on the shared volume
        - `Direct → Llm`: Harness reads file, chunks into working memory if large
        - `Llm → Direct`: Harness writes LLM output to shared volume
        - `Llm → Llm`: Data stays in working memory, no file round-trip

        **Worked example — find an event and get its details:**

        The pattern is list → LLM-pluck → fetch. The middle `Llm` step reads a JSON
        blob and outputs a small top-level object whose keys match the downstream
        step's required params.

        ```json
        {
          "description": "Find 'Microsoft AI Demonstration' and fetch its details",
          "steps": [
            {
              "id": "list",
              "mode": "Direct",
              "gateway": "Mcp",
              "server": "calendar-mcp",
              "tool": "get_calendar_events",
              "params": {
                "accountId": "xebia",
                "timeZone": "America/Chicago",
                "startDate": "2026-04-23",
                "endDate": "2026-04-23"
              },
              "output_to": "wisp-data/events.json"
            },
            {
              "id": "pick",
              "mode": "Llm",
              "prompt": "From the events JSON, find the event whose subject is 'Microsoft AI Demonstration'. Return ONLY a single JSON object with string fields accountId, calendarId, eventId — no prose, no code fences.",
              "input_from": "wisp-data/events.json",
              "output_to": "wisp-data/target.json"
            },
            {
              "id": "details",
              "mode": "Direct",
              "gateway": "Mcp",
              "server": "calendar-mcp",
              "tool": "get_calendar_event_details",
              "params": {
                "accountId":  "{{steps.pick.result.accountId}}",
                "calendarId": "{{steps.pick.result.calendarId}}",
                "eventId":    "{{steps.pick.result.eventId}}",
                "timeZone":   "America/Chicago"
              }
            }
          ]
        }
        ```

        Note how `pick`'s output shape is chosen so `details`'s field-paths match
        directly. If `pick` returned a nested object like `{"event":{"id":"..."}}`, the
        field-paths would need to be `{{steps.pick.result.event.id}}` etc.

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

        ### Failure modes you will see

        Three concrete patterns appear in batch output when a wisp step fails. Read the
        message — it tells you exactly what to fix.

        **Schema validation rejected the params before the call.** The validator read
        the target tool's parameter schema from the registry and the supplied `params`
        didn't match:

        ```
        - `wisp-xyz`: "Fetch events" [failed] (12ms)
          Error (Structural): Params for calendar-mcp/get_calendar_events did not match
          the tool's schema. Missing required field(s): accountId, timeZone. Expected shape:
            accountId: string (required)
            calendarId: string
            timeMin: string
            timeMax: string
            timeZone: string (required)
          Tool: calendar-mcp/get_calendar_events
        ```

        Fix: add the missing fields to `params`. The expected shape is right there —
        you don't need to call `mcp_get_service_details` again.

        **Server returned a soft error.** The MCP call succeeded at the transport level
        (HTTP 200) but the server's JSON body contained an `error` field. The runner
        surfaces these as step failures instead of letting the error text propagate into
        the next step's input:

        ```
        - `wisp-xyz`: "Fetch events" [failed] (13ms)
          Error (Structural): accountId is required
          Tool: mcp_invoke_tool
        ```

        Fix: usually the same as above — add the missing field. Soft errors commonly
        appear when the server enforces a conditional requirement that isn't in the
        declared JSON schema (e.g. "accountId required unless calendarId provided").

        **Template failed to resolve, and the downstream call choked on the literal.**
        You wrote `{{steps.pick.result.eventId}}` but `pick`'s output wasn't a JSON
        object with an `eventId` key. The literal `{{...}}` string went to the tool,
        which returned a soft error:

        ```
        - `wisp-xyz`: "Fetch details" [failed] (8ms)
          Error (Data): Event '{{steps.pick.result.eventId}}' not found
          Tool: mcp_invoke_tool
        ```

        Fix: check what the upstream step actually produced (the batch summary shows
        each step's output). Common causes — the `Llm` pick step wrapped its JSON in
        code fences or prose, or the shape is nested (`{"event":{...}}` instead of the
        flat object you templated against). Tighten the `Llm` step's prompt to emit
        ONLY a bare JSON object with the exact keys you reference downstream.

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
