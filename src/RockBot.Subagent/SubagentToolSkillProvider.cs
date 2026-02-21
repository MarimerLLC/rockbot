using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// Provides a skill guide for subagent management tools.
/// </summary>
public sealed class SubagentToolSkillProvider : IToolSkillProvider
{
    public string Name => "subagent";
    public string Summary => "Spawn background subagents for long-running tasks (spawn_subagent, cancel_subagent, list_subagents, whiteboard_*).";

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

        ## WhiteboardWrite
        Write a key-value pair to a shared board. Use board_id to namespace data.

        ## WhiteboardRead
        Read a value from a shared board by key.

        ## WhiteboardList
        List all keys and values on a shared board.

        ## WhiteboardDelete
        Delete a key from a shared board.

        ## Usage pattern
        1. Use spawn_subagent to start a task
        2. Continue the conversation normally while the subagent works
        3. Subagent progress and results arrive as messages in this session
        4. Use whiteboard tools to share data between your session and the subagent
        """;
}
