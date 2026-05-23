namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// UI-tier listener for <see cref="WorkIqAuthExpired"/> notifications from
/// the agent. Components (banners, dialogs) subscribe to <see cref="Expired"/>
/// to surface a reconnect prompt.
/// </summary>
public interface IWorkIqAuthStatusListener
{
    /// <summary>
    /// Fires every time the agent publishes <see cref="WorkIqAuthExpired"/>.
    /// </summary>
    event EventHandler<WorkIqAuthExpired> Expired;

    /// <summary>
    /// The most recent expiration notification received, or <c>null</c> if
    /// none has been received in this UI session.
    /// </summary>
    WorkIqAuthExpired? LastExpired { get; }

    /// <summary>
    /// Clears the most recent expiration notification — call when the user
    /// has acknowledged it or completed a successful re-consent.
    /// </summary>
    void ClearLastExpired();
}
