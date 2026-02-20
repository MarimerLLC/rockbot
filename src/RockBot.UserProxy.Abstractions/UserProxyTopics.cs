namespace RockBot.UserProxy;

/// <summary>
/// Well-known topic names for user proxy messaging.
/// </summary>
public static class UserProxyTopics
{
    public const string UserMessage = "user.message";
    public const string UserResponse = "user.response";
    public const string ConversationHistoryRequest = "user.history.request";
    public const string ConversationHistoryResponse = "user.history.response";
    public const string UserFeedback = "user.feedback";
}
