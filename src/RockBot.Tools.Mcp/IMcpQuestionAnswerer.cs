namespace RockBot.Tools.Mcp;

/// <summary>
/// Hands the user's own answer to a handed-back MCP question to the bridge — the chat-form path,
/// as opposed to the agent's <c>mcp_answer</c>. Implemented by <see cref="McpManagementExecutor"/>.
/// </summary>
public interface IMcpQuestionAnswerer
{
    /// <summary>
    /// Answers (or declines) <paramref name="questionId"/> for the conversation whose run namespace
    /// is <paramref name="sessionId"/> (<c>session/{id}</c>). The bridge checks it as it checks
    /// <c>mcp_answer</c>. Returns its response: an error when refused, otherwise the resumed
    /// call's outcome.
    /// </summary>
    Task<McpAnswerQuestionResponse> AnswerFromUserAsync(
        string sessionId, string questionId, string? answersJson, bool decline, CancellationToken ct);
}
