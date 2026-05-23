using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// Tracks whether the WorkIQ MSAL cache can currently produce tokens. Other
/// components consult <see cref="IsHealthy"/> to decide whether to expose
/// WorkIQ-backed tools to the LLM; when unhealthy, those tools are filtered
/// out of the published tool list so patrols and chat sessions never invoke
/// them and get back stale <c>auth_required</c> errors.
/// </summary>
/// <remarks>
/// <para>
/// The tracker is per-process. On agent startup it inspects the cache file
/// on disk: a non-empty file implies the user has consented at least once
/// and we optimistically mark healthy. The first failed silent refresh will
/// flip it back to unhealthy via <see cref="MarkUnhealthy"/>, and the first
/// successful cache write — either from MSAL rotation or from a UI-pushed
/// <c>WorkIqAuthCacheUpdated</c> — flips it back to healthy.
/// </para>
/// </remarks>
public sealed class WorkIqHealthTracker
{
    private readonly ILogger<WorkIqHealthTracker> _logger;
    private readonly object _gate = new();

    private bool _isHealthy;
    private string? _lastReason;

    public WorkIqHealthTracker(
        IOptions<MsalTokenProviderOptions> options,
        ILogger<WorkIqHealthTracker> logger)
    {
        _logger = logger;

        // Optimistically assume healthy if the cache file exists and is non-empty —
        // it means the user consented at some point. The first failed refresh will
        // correct the state via MarkUnhealthy.
        var path = options.Value.CacheFilePath;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > 0)
            {
                _isHealthy = true;
                _lastReason = "startup_cache_present";
            }
        }
        catch
        {
            // Best-effort startup inspection — stay unhealthy on any IO error.
        }
    }

    /// <summary>True when the agent believes a silent token refresh can succeed.</summary>
    public bool IsHealthy
    {
        get { lock (_gate) return _isHealthy; }
    }

    /// <summary>Most recent reason string for diagnostics.</summary>
    public string? LastReason
    {
        get { lock (_gate) return _lastReason; }
    }

    /// <summary>
    /// Raised on every transition (both healthy → unhealthy and unhealthy → healthy).
    /// Subscribers run synchronously on the thread that called <see cref="MarkHealthy"/>
    /// or <see cref="MarkUnhealthy"/>; keep handlers cheap.
    /// </summary>
    public event Action<HealthChangedArgs>? HealthChanged;

    /// <summary>
    /// Flip to healthy. No-op when already healthy. Called by
    /// <see cref="TokenCacheStore"/> after a successful cache write.
    /// </summary>
    public void MarkHealthy(string reason)
    {
        Transition(newHealthy: true, reason);
    }

    /// <summary>
    /// Flip to unhealthy. No-op when already unhealthy. Called by
    /// <see cref="MsalTokenProvider"/> when silent refresh fails with
    /// <see cref="Microsoft.Identity.Client.MsalUiRequiredException"/>.
    /// </summary>
    public void MarkUnhealthy(string reason)
    {
        Transition(newHealthy: false, reason);
    }

    private void Transition(bool newHealthy, string reason)
    {
        bool changed;
        bool oldHealthy;
        lock (_gate)
        {
            oldHealthy = _isHealthy;
            changed = oldHealthy != newHealthy;
            _isHealthy = newHealthy;
            _lastReason = reason;
        }

        if (!changed) return;

        _logger.LogInformation(
            "WorkIQ auth health changed: Healthy={Old} → {New}. Reason: {Reason}.",
            oldHealthy, newHealthy, reason);

        HealthChanged?.Invoke(new HealthChangedArgs(oldHealthy, newHealthy, reason));
    }

    /// <summary>Arguments for <see cref="HealthChanged"/>.</summary>
    public sealed record HealthChangedArgs(bool OldValue, bool NewValue, string Reason);
}
