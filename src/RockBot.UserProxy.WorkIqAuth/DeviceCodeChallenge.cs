namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// User-facing challenge surfaced by the device-code flow. Callers render the
/// <see cref="UserCode"/> and <see cref="VerificationUrl"/> for the user, who
/// completes sign-in in a separate browser tab.
/// </summary>
public sealed record DeviceCodeChallenge(
    string UserCode,
    string VerificationUrl,
    DateTimeOffset ExpiresOn,
    string Message);
