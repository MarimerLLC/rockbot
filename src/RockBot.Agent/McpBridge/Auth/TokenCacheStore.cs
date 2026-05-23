using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Messaging;

namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// Persists the MSAL token cache to the agent's PVC. Bridges the UI tier
/// (which performs interactive consent and publishes <see cref="WorkIqAuthCacheUpdated"/>)
/// and the in-process <see cref="IPublicClientApplication"/> that performs silent
/// refresh on every Work IQ request.
/// </summary>
/// <remarks>
/// <para>Two write paths into <see cref="MsalTokenProviderOptions.CacheFilePath"/>:</para>
/// <list type="number">
/// <item>UI tier publishes a fresh cache after consent — handled by <see cref="HandleCacheUpdatedAsync"/>.</item>
/// <item>
/// MSAL rotates the cache during silent refresh — handled by
/// <see cref="OnAfterAccess"/>, registered via
/// <see cref="ITokenCache.SetAfterAccess"/> when this service starts.
/// </item>
/// </list>
/// </remarks>
public sealed class TokenCacheStore : IHostedService
{
    private readonly IMessageSubscriber _subscriber;
    private readonly IPublicClientApplication _msal;
    private readonly MsalTokenProviderOptions _options;
    private readonly ILogger<TokenCacheStore> _logger;

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private ISubscription? _subscription;

    public TokenCacheStore(
        IMessageSubscriber subscriber,
        IPublicClientApplication msal,
        IOptions<MsalTokenProviderOptions> options,
        ILogger<TokenCacheStore> logger)
    {
        _subscriber = subscriber;
        _msal = msal;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureCacheDirectory();

        // Wire MSAL cache callbacks so silent-refresh rotations are persisted.
        _msal.UserTokenCache.SetBeforeAccess(OnBeforeAccess);
        _msal.UserTokenCache.SetAfterAccess(OnAfterAccess);

        // Subscribe to UI-published cache updates.
        _subscription = await _subscriber.SubscribeAsync(
            WorkIqAuthTopics.CacheUpdated,
            "agent.workiq.cache",
            HandleCacheUpdatedAsync,
            cancellationToken);

        _logger.LogInformation(
            "TokenCacheStore ready (cache path: {Path})", _options.CacheFilePath);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private async Task<MessageResult> HandleCacheUpdatedAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var message = envelope.GetPayload<WorkIqAuthCacheUpdated>();
        if (message is null || message.CacheBytes.Length == 0)
        {
            _logger.LogWarning("Received empty WorkIqAuthCacheUpdated; ignoring");
            return MessageResult.DeadLetter;
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            await File.WriteAllBytesAsync(_options.CacheFilePath, message.CacheBytes, ct);
            ApplyRestrictivePermissions(_options.CacheFilePath);
            _logger.LogInformation(
                "Persisted WorkIQ token cache for account {AccountId} ({Bytes} bytes, {Scopes} scope(s))",
                message.AccountId, message.CacheBytes.Length, message.Scopes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist WorkIQ token cache to {Path}", _options.CacheFilePath);
            return MessageResult.Retry;
        }
        finally
        {
            _fileLock.Release();
        }

        return MessageResult.Ack;
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        // MSAL calls this before reading the in-memory cache. Load from disk if present.
        if (!File.Exists(_options.CacheFilePath)) return;

        try
        {
            var bytes = File.ReadAllBytes(_options.CacheFilePath);
            if (bytes.Length > 0)
                args.TokenCache.DeserializeMsalV3(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load WorkIQ token cache from {Path}; treating as empty",
                _options.CacheFilePath);
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        // MSAL only signals HasStateChanged when the cache actually changed
        // (e.g. silent refresh-token rotation). Skip the write otherwise.
        if (!args.HasStateChanged) return;

        try
        {
            var bytes = args.TokenCache.SerializeMsalV3();
            _fileLock.Wait();
            try
            {
                File.WriteAllBytes(_options.CacheFilePath, bytes);
                ApplyRestrictivePermissions(_options.CacheFilePath);
            }
            finally
            {
                _fileLock.Release();
            }
            _logger.LogDebug(
                "Persisted MSAL cache rotation ({Bytes} bytes)", bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist rotated MSAL cache to {Path}",
                _options.CacheFilePath);
        }
    }

    private void EnsureCacheDirectory()
    {
        var dir = Path.GetDirectoryName(_options.CacheFilePath);
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
            ApplyRestrictiveDirectoryPermissions(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create cache directory {Dir}; cache persistence may fail",
                dir);
        }
    }

    private static void ApplyRestrictivePermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort; the file is on a private PVC so this is defense-in-depth.
        }
    }

    private static void ApplyRestrictiveDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best-effort; the directory lives on a private PVC.
        }
    }
}
