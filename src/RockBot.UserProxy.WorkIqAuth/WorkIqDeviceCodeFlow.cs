using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// Drives the MSAL device-code flow from the UI tier and ships the resulting
/// token cache to the agent via <see cref="WorkIqAuthCacheUpdated"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why device-code for both Blazor and CLI: the Blazor app is server-rendered
/// in a pod where MSAL cannot reach the user's browser via a loopback redirect.
/// Device-code only needs the user to type a code into a separate browser tab,
/// so it works identically from any UI surface.
/// </para>
/// <para>
/// The flow holds the MSAL cache only for the lifetime of the call. Once the
/// cache is published, the UI's <see cref="IPublicClientApplication"/> is
/// disposed and no credential material remains on the UI side.
/// </para>
/// </remarks>
public sealed class WorkIqDeviceCodeFlow
{
    private readonly WorkIqClientSettings _settings;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<WorkIqDeviceCodeFlow> _logger;

    public WorkIqDeviceCodeFlow(
        IOptions<WorkIqClientSettings> settings,
        IMessagePublisher publisher,
        ILogger<WorkIqDeviceCodeFlow> logger)
    {
        _settings = settings.Value;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Begins the device-code flow. Returns the user-facing challenge as soon
    /// as MSAL produces it, along with a completion task the caller awaits
    /// until the user finishes sign-in (or the flow fails).
    /// </summary>
    public async Task<(DeviceCodeChallenge Challenge, Task Completion)> BeginAsync(
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            throw new WorkIqAuthFlowException(
                WorkIqAuthFlowException.Codes.NotConfigured,
                "WorkIQ is not configured. Set WorkIQ:TenantId and WorkIQ:ClientId before running this flow.");
        }

        var app = BuildPublicClientApp();
        var challengeTcs = new TaskCompletionSource<DeviceCodeChallenge>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Capture the serialized cache via MSAL's SetAfterAccess callback. The
        // callback fires inside ExecuteAsync once the new tokens are cached.
        byte[] capturedCache = [];
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                capturedCache = args.TokenCache.SerializeMsalV3();
        });

        var completion = Task.Run(async () =>
        {
            try
            {
                var result = await app.AcquireTokenWithDeviceCode(_settings.Scopes, dcr =>
                {
                    challengeTcs.TrySetResult(new DeviceCodeChallenge(
                        UserCode: dcr.UserCode,
                        VerificationUrl: dcr.VerificationUrl,
                        ExpiresOn: dcr.ExpiresOn,
                        Message: dcr.Message));
                    return Task.CompletedTask;
                }).ExecuteAsync(cancellationToken);

                var bytes = SerializeCacheOverride is { } overrideFn
                    ? overrideFn(app)
                    : capturedCache;
                await PublishCacheAsync(bytes, result.Account, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new WorkIqAuthFlowException(
                    WorkIqAuthFlowException.Codes.UserCancelled,
                    "Device-code sign-in was cancelled.");
            }
            catch (MsalClientException ex) when (ex.ErrorCode is "code_expired"
                or "authorization_pending_timeout"
                or "verification_code_expired")
            {
                throw new WorkIqAuthFlowException(
                    WorkIqAuthFlowException.Codes.ChallengeExpired,
                    "The device code expired before sign-in was completed. Start over.",
                    ex);
            }
            catch (MsalException ex)
            {
                throw new WorkIqAuthFlowException(
                    WorkIqAuthFlowException.Codes.MsalError,
                    $"Microsoft identity service returned an error: {ex.ErrorCode}",
                    ex);
            }
            catch (WorkIqAuthFlowException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new WorkIqAuthFlowException(
                    WorkIqAuthFlowException.Codes.Unknown,
                    "Unexpected error during WorkIQ device-code flow.",
                    ex);
            }
        }, cancellationToken);

        // If the underlying task faults before MSAL ever issues a code, propagate
        // the failure to the caller waiting on the challenge.
        _ = completion.ContinueWith(t =>
        {
            if (t.IsFaulted && !challengeTcs.Task.IsCompleted)
                challengeTcs.TrySetException(t.Exception!.InnerExceptions);
        }, TaskScheduler.Default);

        var challenge = await challengeTcs.Task;
        return (challenge, completion);
    }

    private IPublicClientApplication BuildPublicClientApp()
    {
        var builder = PublicClientApplicationBuilder.Create(_settings.ClientId);
        if (!string.IsNullOrWhiteSpace(_settings.Authority))
            builder = builder.WithAuthority(_settings.Authority);
        else
            builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, _settings.TenantId);
        return builder.Build();
    }

    private async Task PublishCacheAsync(byte[] bytes, IAccount? account, CancellationToken ct)
    {
        try
        {
            var message = new WorkIqAuthCacheUpdated
            {
                CacheBytes = bytes,
                AccountId = account?.HomeAccountId?.Identifier
                    ?? account?.Username
                    ?? "unknown",
                Scopes = [.. _settings.Scopes]
            };

            var envelope = message.ToEnvelope(source: "ui.workiq");
            await _publisher.PublishAsync(WorkIqAuthTopics.CacheUpdated, envelope, ct);

            _logger.LogInformation(
                "Published WorkIQ token cache for account {AccountId} ({Bytes} bytes)",
                message.AccountId, bytes.Length);
        }
        catch (Exception ex)
        {
            throw new WorkIqAuthFlowException(
                WorkIqAuthFlowException.Codes.PublishFailed,
                "WorkIQ sign-in completed but the agent could not be notified. " +
                "Verify the message bus is reachable and retry.",
                ex);
        }
    }

    /// <summary>
    /// Test seam: when set, used instead of MSAL's real cache serialization.
    /// Production code should leave this null.
    /// </summary>
    internal Func<IPublicClientApplication, byte[]>? SerializeCacheOverride { get; set; }
}
