using RockBot.UserProxy.WorkIqAuth;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Scoped state holder for WorkIQ connection state in the Blazor UI.
/// Components bind to <see cref="StateChanged"/> to re-render when status
/// transitions occur (connect started, completed, or expired).
/// </summary>
public sealed class WorkIqAuthUiService : IDisposable
{
    private readonly IWorkIqAuthStatusListener _listener;

    public WorkIqAuthUiService(IWorkIqAuthStatusListener listener)
    {
        _listener = listener;
        _listener.Expired += OnExpired;

        // Initialize from any expiration that happened before this circuit started.
        if (_listener.LastExpired is { } existing)
            CurrentReason = existing.Reason;
    }

    /// <summary>
    /// Fires when the connection state or banner content changes.
    /// Components handle this to call StateHasChanged.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// When true, the banner is visible. Cleared by <see cref="DismissExpiredBanner"/>
    /// or after a successful re-consent via <see cref="MarkConnected"/>.
    /// </summary>
    public bool IsExpiredBannerVisible { get; private set; }

    /// <summary>
    /// Most recent expiration reason, surfaced in the banner.
    /// </summary>
    public string? CurrentReason { get; private set; }

    /// <summary>Hide the banner; called when the user dismisses it.</summary>
    public void DismissExpiredBanner()
    {
        if (!IsExpiredBannerVisible) return;
        IsExpiredBannerVisible = false;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Called by the connect component on successful sign-in to clear any
    /// outstanding expired notification.
    /// </summary>
    public void MarkConnected()
    {
        _listener.ClearLastExpired();
        IsExpiredBannerVisible = false;
        CurrentReason = null;
        StateChanged?.Invoke();
    }

    private void OnExpired(object? sender, WorkIqAuthExpired e)
    {
        CurrentReason = e.Reason;
        IsExpiredBannerVisible = true;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _listener.Expired -= OnExpired;
    }
}
