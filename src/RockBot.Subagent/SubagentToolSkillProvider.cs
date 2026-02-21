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
        Both you and the subagent have full access to long-term memory. Use the category
        convention 'subagent-whiteboards/{task_id}' as a per-subagent scratchpad:

        - You write input data before spawning:
            SaveMemory(content="...", category="subagent-whiteboards/{task_id}")
        - The subagent reads it with SearchMemory(query="...", category="subagent-whiteboards/{task_id}")
        - The subagent writes results back to the same category
        - You read them back after receiving the result message

        Clean up with DeleteMemory when done.

        ## Usage pattern
        1. (Optional) Write input data to 'subagent-whiteboards/{task_id}' before spawning
        2. Use spawn_subagent to start the task — tell the subagent the task_id to read from
        3. Continue the conversation normally while the subagent works
        4. Subagent progress and final result arrive as messages in this session
        5. (Optional) Read output data from 'subagent-whiteboards/{task_id}'
        """;
}
