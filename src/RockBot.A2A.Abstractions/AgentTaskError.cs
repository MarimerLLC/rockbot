namespace RockBot.A2A;

/// <summary>
/// Error response for a failed agent-to-agent operation.
/// Published to the ReplyTo topic.
/// </summary>
public sealed record AgentTaskError
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool IsRetryable { get; init; }

    public static class Codes
    {
        public const string TaskNotFound = "task_not_found";
        public const string TaskNotCancelable = "task_not_cancelable";
        public const string SkillNotSupported = "skill_not_supported";
        public const string ExecutionFailed = "execution_failed";
        public const string InvalidRequest = "invalid_request";
    }
}
