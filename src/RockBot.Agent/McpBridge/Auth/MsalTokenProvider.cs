using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// MSAL-backed <see cref="ITokenProvider"/> for the <c>workiq</c> profile.
/// Acquires tokens silently from a cache populated by the UI tier; on cache
/// invalidation, publishes <see cref="WorkIqAuthExpired"/> so the UI can
/// prompt the user to reconnect.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    private readonly IPublicClientApplication _msal;
    private readonly MsalTokenProviderOptions _options;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<MsalTokenProvider> _logger;

    public MsalTokenProvider(
        IPublicClientApplication msal,
        IOptions<MsalTokenProviderOptions> options,
        IMessagePublisher publisher,
        ILogger<MsalTokenProvider> logger)
    {
        _msal = msal;
        _options = options.Value;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IEnumerable<IAccount> accounts;
        try
        {
            accounts = await _msal.GetAccountsAsync().WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new TokenAcquisitionException(
                TokenAcquisitionException.Codes.Unknown,
                "Failed to enumerate MSAL accounts.",
                ex);
        }

        var account = accounts.FirstOrDefault();
        if (account is null)
        {
            throw new TokenAcquisitionException(
                TokenAcquisitionException.Codes.NotAuthenticated,
                "No Work IQ account has consented yet. Open the UI and click 'Connect M365' to complete initial authentication.");
        }

        try
        {
            var builder = _msal.AcquireTokenSilent(_options.Scopes, account);
            if (forceRefresh) builder = builder.WithForceRefresh(true);
            var result = await builder.ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.LogWarning(ex,
                "MSAL silent token acquisition for account {AccountId} requires interactive re-consent ({Classification})",
                account.HomeAccountId?.Identifier, ex.Classification);

            await PublishExpiredAsync(account, ex.Message, cancellationToken);

            throw new TokenAcquisitionException(
                TokenAcquisitionException.Codes.ReauthRequired,
                "Work IQ token refresh failed — the user must re-consent. " +
                "Open the UI and click 'Reconnect M365' to restore access.",
                ex);
        }
        catch (MsalServiceException ex)
        {
            _logger.LogWarning(ex,
                "MSAL service error during silent token acquisition: {ErrorCode}", ex.ErrorCode);
            throw new TokenAcquisitionException(
                TokenAcquisitionException.Codes.IdentityProviderUnreachable,
                $"Microsoft identity service returned an error ({ex.ErrorCode}). This may be transient.",
                ex);
        }
        catch (TokenAcquisitionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TokenAcquisitionException(
                TokenAcquisitionException.Codes.Unknown,
                "Unexpected error acquiring Work IQ access token.",
                ex);
        }
    }

    private async Task PublishExpiredAsync(IAccount account, string reason, CancellationToken ct)
    {
        try
        {
            var message = new WorkIqAuthExpired
            {
                AccountId = account.HomeAccountId?.Identifier ?? account.Username ?? "unknown",
                Reason = reason
            };
            var envelope = message.ToEnvelope(source: "agent.workiq");
            await _publisher.PublishAsync(WorkIqAuthTopics.Expired, envelope, ct);
        }
        catch (Exception ex)
        {
            // Do not let publication failure shadow the original auth failure —
            // the caller still needs to see the TokenAcquisitionException.
            _logger.LogWarning(ex, "Failed to publish WorkIqAuthExpired notification");
        }
    }
}
