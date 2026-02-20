namespace RockBot.Host;

/// <summary>
/// Options for the long-term conversation log used by the preference-inference dream pass.
/// </summary>
public sealed class ConversationLogOptions
{
    /// <summary>
    /// Directory for the conversation log file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string BasePath { get; set; } = "conversation-log";
}
