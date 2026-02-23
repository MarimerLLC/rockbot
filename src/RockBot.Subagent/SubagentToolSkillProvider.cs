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

        ## spawn_subagent
        Spawn an isolated background subagent to handle a long-running or complex task.
        The subagent runs independently and reports progress + final result back to you.
        Use this when a task involves many tool calls or extended processing time.

        Parameters:
        - description (required): Detailed instructions for what the subagent should do
        - context (optional): Additional data or context the subagent needs
        - timeout_minutes (optional): How long to allow (default 10 minutes)

        Returns: task_id — use this to track or cancel the subagent.

        ## cancel_subagent
        Cancel a running subagent by its task_id.

        ## list_subagents
        List all currently running subagent tasks.

        ## Sharing data with a subagent (whiteboard convention)
        Both you and the subagent have full access to long-term memory. The category
        'subagent-whiteboards/{task_id}' is the per-subagent scratchpad:

        - Before spawning: write input data the subagent needs
            SaveMemory(content="...", category="subagent-whiteboards/{task_id}")
        - The subagent reads input and writes results back to the same category with
          tag 'subagent-whiteboard' — its system prompt instructs it to do this automatically
        - After receiving the completion message: read results with
            SearchMemory(category="subagent-whiteboards/{task_id}")

        Whiteboard entries persist in long-term memory after the task completes so you can
        reference them across multiple conversation turns. They are cleaned up by the dream
        service as normal stale-memory consolidation.

        ## Usage pattern
        1. (Optional) Write input data to 'subagent-whiteboards/{task_id}' before spawning
        2. Use spawn_subagent — include the task_id in the description if the subagent needs input
        3. Continue conversation normally; progress and final result arrive as messages
        4. After the completion message, search 'subagent-whiteboards/{task_id}' for detailed output
        """;
}
