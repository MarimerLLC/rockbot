using Microsoft.AspNetCore.Authorization;

namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Authorization requirement satisfied only by a principal on the configured allowlist.
/// </summary>
public sealed class AllowlistRequirement : IAuthorizationRequirement
{
    /// <summary>Name of the policy this requirement backs; also the default policy when auth is on.</summary>
    public const string PolicyName = "RockBotAllowlist";
}

/// <summary>
/// Evaluates <see cref="AllowlistRequirement"/> against <see cref="UserAllowlist"/>.
/// </summary>
/// <remarks>
/// The check lives in an authorization handler rather than in the OAuth ticket callback so it runs
/// on every request, not once at sign-in. Removing someone from the allowlist then takes effect on
/// their next request instead of whenever their cookie happens to expire.
/// </remarks>
public sealed class AllowlistAuthorizationHandler : AuthorizationHandler<AllowlistRequirement>
{
    private readonly UserAllowlist _allowlist;
    private readonly ILogger<AllowlistAuthorizationHandler> _logger;

    public AllowlistAuthorizationHandler(UserAllowlist allowlist, ILogger<AllowlistAuthorizationHandler> logger)
    {
        _allowlist = allowlist;
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AllowlistRequirement requirement)
    {
        if (_allowlist.IsAllowed(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Logged rather than silently 403'd: "signed in with the wrong account" is by far the
            // most common cause, and an operator adding someone to the allowlist needs to see the
            // address the provider actually sent.
            _logger.LogWarning(
                "Denied sign-in for {Email} — not on the allowlist.",
                UserAllowlist.GetEmail(context.User) ?? "(no email claim)");
        }

        return Task.CompletedTask;
    }
}
