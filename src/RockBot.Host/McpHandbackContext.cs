using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Whether the current run can answer a question an MCP server hands back mid-call
/// (<c>design/mcp-elicitation-handback.md</c>). Ambient per agent loop run, like
/// <see cref="TypedToolSurfaceContext"/>: <see cref="AgentLoopRunner.RunAsync"/> sets it from the
/// run's tool list, and the MCP gateway reads it when it makes a downstream call, so a server's
/// question only comes back to a run that has <see cref="AnswerToolName"/> to answer it with.
/// </summary>
/// <remarks>
/// Code that makes MCP calls on a run's behalf without being that run — a wisp's deterministic
/// steps execute inside the parent's tool call — must <see cref="Suppress"/> it, or the parent's
/// capability would leak to a caller that can't answer.
/// </remarks>
public static class McpHandbackContext
{
    /// <summary>The management tool that answers a handed-back question.</summary>
    public const string AnswerToolName = "mcp_answer";

    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>True when the current run offers <see cref="AnswerToolName"/>.</summary>
    public static bool CanAnswer => Current.Value;

    /// <summary>Makes the run's capability ambient: it can answer when its tools include <see cref="AnswerToolName"/>.</summary>
    public static IDisposable Set(ChatOptions? options) =>
        Set(options?.Tools?.Any(t => t.Name == AnswerToolName && t.GetService<ICallerScopedTool>() is null) == true);

    /// <summary>Marks the current flow as unable to answer until the result is disposed.</summary>
    public static IDisposable Suppress() => Set(false);

    private static IDisposable Set(bool canAnswer)
    {
        var previous = Current.Value;
        Current.Value = canAnswer;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
