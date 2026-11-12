namespace RockBot.UserProxy.ChatState.Tests;

/// <summary>Builders for recorded event sequences, all on a fixed clock.</summary>
internal static class ChatEventFixtures
{
    public const string Session = "blazor-session";
    public const string Agent = "RockBot";

    public static readonly DateTime T0 = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>The fixed time <paramref name="seconds"/> after <see cref="T0"/>.</summary>
    public static DateTime At(double seconds) => T0.AddSeconds(seconds);

    public static AgentReply Reply(
        string content,
        bool isFinal = true,
        string agentName = Agent,
        string sessionId = Session,
        string? followsQuestionId = null) => new()
    {
        Content = content,
        SessionId = sessionId,
        AgentName = agentName,
        IsFinal = isFinal,
        FollowsQuestionId = followsQuestionId,
    };

    public static McpQuestionForm Form(string questionId, DateTime expiresAt) => new()
    {
        QuestionId = questionId,
        ServerName = "calendar",
        ToolName = "create_event",
        Message = "Which calendar?",
        ExpiresAt = new DateTimeOffset(expiresAt),
    };

    public static McpQuestionReceived Question(string questionId, double atSeconds, double expiresInSeconds = 600) =>
        new(Reply("Please fill in the form.", agentName: "mcp-bridge"), Form(questionId, At(atSeconds + expiresInSeconds)))
        {
            Timestamp = At(atSeconds),
        };

    public static McpQuestionStatusChanged QuestionStatus(string questionId, string state, double atSeconds, string? reason = null) =>
        new(new McpQuestionStatus { SessionId = Session, QuestionId = questionId, State = state, Reason = reason })
        {
            Timestamp = At(atSeconds),
        };
}
