namespace RockBot.Host;

/// <summary>
/// Options for the heartbeat patrol bootstrap service.
/// </summary>
public sealed class HeartbeatBootstrapOptions
{
    /// <summary>
    /// Whether to automatically register the heartbeat patrol task on startup.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cron expression controlling how often the patrol fires.
    /// Defaults to every 30 minutes.
    /// </summary>
    public string CronExpression { get; set; } = "*/30 * * * *";
}
