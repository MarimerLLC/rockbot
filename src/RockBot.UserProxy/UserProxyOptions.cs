namespace RockBot.UserProxy;

/// <summary>
/// Configuration options for the user proxy service.
/// </summary>
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; } = "user-proxy";
    public TimeSpan DefaultReplyTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
