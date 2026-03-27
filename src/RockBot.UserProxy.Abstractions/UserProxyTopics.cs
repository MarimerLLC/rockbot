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
    public const string AgentInfoRequest = "agent.info.request";
    public const string AgentInfoResponse = "agent.info.response";

    // Saved responses
    public const string SaveResponseRequest = "user.saved.save.request";
    public const string SaveResponseAck = "user.saved.save.ack";
    public const string ListSavedResponsesRequest = "user.saved.list.request";
    public const string ListSavedResponsesResponse = "user.saved.list.response";
    public const string GetSavedResponseRequest = "user.saved.get.request";
    public const string GetSavedResponseResponse = "user.saved.get.response";
    public const string DeleteSavedResponseRequest = "user.saved.delete.request";
    public const string DeleteSavedResponseAck = "user.saved.delete.ack";
}
