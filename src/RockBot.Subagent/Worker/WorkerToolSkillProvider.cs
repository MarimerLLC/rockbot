using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Tool guide for <c>spawn_workers</c>, explaining when to pick a worker over a wisp
/// or subagent and how to consume worker receipts.
/// </summary>
public sealed class WorkerToolSkillProvider : IToolSkillProvider
{
    public string Name => "worker";

    public string Summary => "Spawn lean worker subagents for focused gather tasks (spawn_workers).";

    public string GetDocument() =>
        """
        # Worker Tools Guide

        Workers are the LEAN rung between `spawn_wisps` and `spawn_subagent`. They run
        a slim LLM loop — no long-term-memory injection, no episodic recall, no
        identity entries, no knowledge graph, no completion re-prompting, Low-tier
        model, tight iteration cap (12 by default).

        ## When to pick which tool

        | Tool             | Use when                                                                 |
        |------------------|--------------------------------------------------------------------------|
        | spawn_wisps      | Steps are deterministic — no LLM needed to interpret results.            |
        | spawn_workers    | LLM needs to interpret tool results and branch, but does NOT need        |
        |                  | persona, history, or long-term memory. Mechanical gather work.           |
        | spawn_subagent   | Task is deliberative, persona-bearing, or open-ended.                    |

        ## spawn_workers parameters

        - `description` (required) — one-sentence imperative.
        - `context` (optional) — pre-resolved facts the worker treats as ground truth.
        - `result_key` (optional) — override the auto-assigned `worker/<task-id>/result`
          working-memory key. Use this when you want the worker to overwrite a known
          shared key directly (e.g. `shared/patrol/calendar-latest`).
        - `input_keys` (optional) — data the worker works from instead of fetching it:
          `wisp:<wisp-id>/<step-id>` for a wisp step's output, or a full working-memory
          key with a `/` (e.g. `wisp/wisp-1234/search/output`, or the key a save
          reported). The worker gets the content as its input and is told not to
          refetch it. Large values are chunked into the worker's namespace. Every key
          must exist, or no worker starts.
        - `timeout_minutes` (optional) — soft wall-clock cap. Default 5.
        - `tools_allow` (optional) — allowlist that narrows the **non-MCP** registry
          tools (`web_search`, `execute_python_script`, `spawn_wisps`, …) the worker
          may invoke — exact names or prefixes. Use it to bound schema-injection cost
          when you know the scope ahead of time. The **MCP gateway is always
          available** (`mcp_list_services`, `mcp_get_service_details`,
          `mcp_invoke_tool`, `mcp_get_prompt`, `mcp_list_resources`,
          `mcp_read_resource`, and `mcp_find_tools` when the worker's
          mode has it) and is never gated by this list, so a value like
          `calendar-mcp.*` never cuts a worker off from MCP. In eager mode it does
          narrow the typed `{server}__{tool}` tools, matched as `{server}.{tool}`.

        ## Fetch once, filter cheaply (wisp → worker)

        When a wisp has already fetched raw data that is too big or noisy to use in
        your own context, hand it to a worker — do not have the worker fetch it again:

        1. `spawn_wisps` runs the searches/reads. Its result lists each wisp's
           `Output key: wisp/<id>/<step>/output`.
        2. `spawn_workers` with `input_keys: ["wisp:<id>/<step>"]` and a description
           such as "Keep only messages from X; return id, subject, date".

        Never brief a worker to re-run a search, listing or read a wisp already did,
        and don't call that tool with the same arguments yourself. A call that repeats
        an earlier fetch in the same run gets a system note saying where the data is.
        For a light filter, an `Llm` step at the end of the wisp itself is cheaper
        still (see the wisp guide).

        ## Consuming a worker batch

        `spawn_workers` returns a batch receipt — each worker's findings inlined under a
        `--- <task-id> (<result_key>) ---` heading, then one `WorkerResult` per definition.
        For each result:

        1. Read the inlined findings straight from the receipt. Only when the heading says
           the value was truncated (or could not be retrieved) do you call
           `get_from_working_memory(result_key)` for the full text. A heading marked
           `NO CONTENT` means the worker saved nothing — report that, do not invent findings.
        2. Review `blocked` — items the worker could not verify, decide whether to
           re-spawn with different parameters or hand back to the user.
        3. Review `converged_patterns` — tool-call sequences the worker observed
           converging on success. For any worth keeping, call `promote_skill_asset`
           yourself (workers cannot promote — you have the skill/identity context
           they lack).

        ## What workers CANNOT do

        - Spawn other workers, subagents, or A2A calls (workers are leaf nodes).
        - Call `save_memory`, `save_skill`, `promote_skill_asset`,
          `update_task_directive`.
        - Reconfigure MCP servers (`mcp_register_server`, `mcp_unregister_server`).

        If your worker needs any of those, you picked the wrong rung — use
        `spawn_subagent` instead.
        """;
}
