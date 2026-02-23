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

        ## Sharing data with a subagent (shared memory)
        Both you and the subagent have access to shared memory — a cross-session scratch
        space that all execution contexts (user sessions, patrol tasks, subagents) can
        read and write. Use SaveToSharedMemory / GetFromSharedMemory / SearchSharedMemory.

        - Before spawning: write input data the subagent needs
            SaveToSharedMemory(key="input-{task_id}", data="...", category="subagent-output")
        - The subagent writes results to shared memory with category 'subagent-output'
          — its system prompt instructs it to do this automatically
        - After receiving the completion message: retrieve results with
            GetFromSharedMemory(key="...") or SearchSharedMemory(category="subagent-output")

        Shared memory entries expire based on TTL (default 30 minutes). They are not
        processed by the LLM or dream service — data is preserved verbatim.

        ## Usage pattern
        1. (Optional) Write input data to shared memory before spawning
        2. Use spawn_subagent — mention the shared memory key in the description if needed
        3. Continue conversation normally; progress and final result arrive as messages
        4. After the completion message, check shared memory for detailed output
        """;
}
