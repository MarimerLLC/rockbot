using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// Provides a skill guide for subagent management tools.
/// </summary>
public sealed class SubagentToolSkillProvider : IToolSkillProvider
{
    public string Name => "subagent";
    public string Summary => "Spawn background subagents for long-running tasks (spawn_subagent, cancel_subagent, list_subagents).";

    public string GetDocument() =>
        """
        # Subagent Tools Guide

        You are an orchestrator. spawn_subagent is your PRIMARY execution mechanism —
        delegate all tool-heavy work to subagents so the user's chat input stays unlocked.

        ## spawn_subagent
        Spawn an isolated background subagent to execute a task. The subagent runs
        independently with its own tool set and reports progress + final result back.

        **Default to using this for any task involving tool calls.** Direct execution
        in your own loop locks the user's input. Subagents free you to stay responsive.

        Parameters:
        - description (required): Detailed, self-contained instructions. The subagent
          has NO conversation history — include all context it needs (names, dates,
          search terms, timezone, expected output format).
        - context (optional): Additional data or context the subagent needs
        - timeout_minutes (optional): How long to allow (default 10 minutes)
        - max_iterations (optional): Maximum tool-calling iterations (default: model-
          specific, typically 25). Increase when the subagent needs many sequential
          tool calls. When the subagent will use spawn_wisps for parallel work, the
          default is usually sufficient since wisps don't consume iteration budget.
        - consolidate (optional, default true): When true, this subagent's result is
          batched with sibling results into a single consolidated response. Set to false
          to deliver this subagent's result immediately as its own independent response.
          Use consolidate: false when the user asks for results "as they come in",
          "one at a time", or indicates they want streaming/immediate delivery.

        Returns: task_id — use this to track or cancel the subagent.

        ## cancel_subagent
        Cancel a running subagent by its task_id.

        ## list_subagents
        List all currently running subagent tasks. You have 3 concurrent slots.

        ## Decomposition patterns
        - **Single delegation**: One subagent for the whole task.
        - **Parallel fan-out**: Spawn 2-3 subagents for independent subtasks (e.g.,
          one for calendar, one for email). Sibling results are automatically
          consolidated into a single unified response by default.
        - **Sequential pipeline**: Spawn one subagent, then spawn the next when its
          result arrives (e.g., find email → schedule follow-up).

        ## Sharing data (whiteboard convention)
        Both you and the subagent share long-term memory. The category
        'subagent-whiteboards/<actual-task-id>' is the per-subagent scratchpad —
        substitute the actual task_id you received from spawn_subagent (the GUID-
        shaped value), never the literal text {task_id}. This is a long-term memory
        CATEGORY (the `category` parameter of save_memory), distinct from the
        subagent's working-memory namespace `subagent/<actual-task-id>`.

        - Before spawning: write input data the subagent needs to that category
        - After the completion message: search that category for detailed outputs

        ## Workflow
        1. Acknowledge the user's request immediately
        2. Spawn subagent(s) with detailed instructions
        3. Return control to the user — your response should take seconds
        4. The system automatically consolidates sibling subagent results into a
           single unified response. You do not need to manually synthesize each
           result as it arrives — just let the consolidation happen.
        """;
}
