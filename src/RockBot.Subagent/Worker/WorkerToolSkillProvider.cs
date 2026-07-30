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
        - `timeout_minutes` (optional) — soft wall-clock cap. Default 5.
        - `tools_allow` (optional) — allowlist that narrows the **non-MCP** registry
          tools (`web_search`, `execute_python_script`, `spawn_wisps`, …) the worker
          may invoke — exact names or prefixes. Use it to bound schema-injection cost
          when you know the scope ahead of time. The **MCP gateway is always
          available** (`mcp_list_services`, `mcp_get_service_details`,
          `mcp_invoke_tool`, `mcp_get_prompt`) and is never gated by this list — every
          external MCP call routes through the gateway, so a value like `calendar-mcp.*`
          does not restrict (or grant) MCP access.

        ## Consuming a worker batch

        `spawn_workers` returns a batch receipt — one `WorkerResult` per definition.
        For each result:

        1. Read the actual findings with `get_from_working_memory(result_key)`.
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
