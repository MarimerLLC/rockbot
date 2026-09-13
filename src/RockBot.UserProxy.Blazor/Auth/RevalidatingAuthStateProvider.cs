using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Re-checks the allowlist on a live Blazor Server circuit and tears the circuit down when the
/// user no longer qualifies.
/// </summary>
/// <remarks>
/// A circuit outlives the request that created it: once the SignalR connection is up, nothing
/// re-runs the authorization pipeline, so an open tab keeps working indefinitely after the user is
/// removed from the allowlist or their cookie expires. Persistent cookies make that window longer,
/// which is what turns this from a nicety into the thing that makes revocation mean something.
/// </remarks>
public sealed class RevalidatingAuthStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly UserAllowlist _allowlist;

    public RevalidatingAuthStateProvider(ILoggerFactory loggerFactory, UserAllowlist allowlist)
        : base(loggerFactory)
    {
        _allowlist = allowlist;
    }

    /// <summary>
    /// Thirty minutes: short enough that revoking access takes effect the same working hour, long
    /// enough not to churn on an idle tab.
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
        => Task.FromResult(_allowlist.IsAllowed(authenticationState.User));
}
