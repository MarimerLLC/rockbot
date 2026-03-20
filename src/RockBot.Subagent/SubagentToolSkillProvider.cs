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

        Returns: task_id — use this to track or cancel the subagent.

        ## cancel_subagent
        Cancel a running subagent by its task_id.

        ## list_subagents
        List all currently running subagent tasks. You have 3 concurrent slots.

        ## Decomposition patterns
        - **Single delegation**: One subagent for the whole task.
        - **Parallel fan-out**: Spawn 2-3 subagents for independent subtasks (e.g.,
          one for calendar, one for email). Synthesize when results arrive.
        - **Sequential pipeline**: Spawn one subagent, then spawn the next when its
          result arrives (e.g., find email → schedule follow-up).

        ## Sharing data (whiteboard convention)
        Both you and the subagent share long-term memory. The category
        'subagent-whiteboards/{task_id}' is the per-subagent scratchpad:

        - Before spawning: write input data the subagent needs
        - After the completion message: search that category for detailed outputs

        ## Workflow
        1. Acknowledge the user's request immediately
        2. Spawn subagent(s) with detailed instructions
        3. Return control to the user — your response should take seconds
        4. When '[Subagent task <id> completed]' arrives, synthesize and present results
        """;
}
