namespace RockBot.AgUi.Client;

/// <summary>
/// The names of the proxy's <c>CUSTOM</c> events, as <c>docs/agui.md</c> lists them.
/// </summary>
/// <remarks>
/// The proxy's own constants are <c>TurnEventMapper.CustomNames</c> in
/// <c>RockBot.UserProxy.AgUi.Events</c>. That library references the message-bus client, which a
/// browser app can't use, so the names are repeated here; a test keeps the two lists equal.
/// </remarks>
public static class RockBotEvents
{
    public const string AgentRunStarted = "rockbot.agent_run_started";
    public const string Heartbeat = "rockbot.heartbeat";
    public const string Attachments = "rockbot.attachments";
    public const string Origin = "rockbot.origin";
    public const string ReplyPending = "rockbot.reply_pending";
    public const string McpQuestion = "rockbot.mcp_question";
    public const string McpQuestionStatus = "rockbot.mcp_question_status";
    public const string RunFinished = "rockbot.run_finished";
    public const string McpSignInStatus = "rockbot.mcp_signin_status";
    public const string AgentName = "rockbot.agent_name";
    public const string Error = "rockbot.error";
    public const string ActiveWork = "rockbot.active_work";
    public const string ContextCleared = "rockbot.context_cleared";
    public const string Connected = "rockbot.connected";
    public const string Resync = "rockbot.resync";

    /// <summary>The <c>ACTIVITY_SNAPSHOT</c> activity type for progress text.</summary>
    public const string ProgressActivityType = "rockbot.progress";

    /// <summary>The task-list key of the primary turn in the <c>STATE_*</c> state.</summary>
    public const string PrimaryTaskListKey = "primary";

    /// <summary><c>RUN_ERROR</c> codes the mapping tells apart; any other code is a failed turn.</summary>
    public static class ErrorCodes
    {
        public const string Error = "error";
        public const string AgentLost = "agent_lost";
    }

    /// <summary>Sessions whose output goes to every thread (<c>docs/agui.md</c>, "Which thread gets what").</summary>
    public static class Sessions
    {
        public const string ScheduledSystem = "scheduled-system";
        public const string A2AInbound = "a2a-inbound";
    }
}
