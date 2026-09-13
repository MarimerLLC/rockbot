namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Display metadata for one identity provider offered on the login page.
/// </summary>
/// <param name="Key">Configuration key, e.g. <c>Google</c>.</param>
/// <param name="Scheme">Authentication scheme name the challenge is issued against.</param>
/// <param name="DisplayName">Label shown on the sign-in button.</param>
public sealed record AuthProvider(string Key, string Scheme, string DisplayName);

/// <summary>
/// The set of identity providers this deployment actually offers — the configured ones, not the
/// supported ones. Registered as a singleton so the login page, the challenge endpoint, and the
/// startup registration all agree on one list; a provider the app did not register must never be
/// offered as a button or accepted as a <c>?provider=</c> value.
/// </summary>
/// <remarks>
/// Only Google ships today, but nothing outside <see cref="Descriptors"/> and the registration
/// switch in <c>AuthSetup</c> knows that. Adding Microsoft or GitHub is a package reference, a
/// descriptor entry, and one registration line.
/// </remarks>
public sealed class AuthProviderRegistry
{
    /// <summary>Every provider this build can register, whether or not it is configured.</summary>
    private static readonly IReadOnlyList<AuthProvider> Descriptors =
    [
        new AuthProvider("Google", GoogleScheme, "Google"),
    ];

    /// <summary>Scheme name registered by <c>AddGoogle()</c>; its callback path is <c>/signin-google</c>.</summary>
    public const string GoogleScheme = "Google";

    private readonly Dictionary<string, AuthProvider> _byKey;

    /// <summary>Builds the registry from bound options, keeping only fully configured providers.</summary>
    public AuthProviderRegistry(AuthOptions options)
    {
        var enabled = options.Enabled
            ? Descriptors.Where(d =>
                options.Providers.TryGetValue(d.Key, out var p) && p is not null && p.IsConfigured)
            : [];

        Enabled = enabled.ToList();
        _byKey = Enabled.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Providers offered by this deployment, in display order.</summary>
    public IReadOnlyList<AuthProvider> Enabled { get; }

    /// <summary>Names of every provider this build supports, for error messages.</summary>
    public static IEnumerable<string> KnownProviders => Descriptors.Select(d => d.Key);

    /// <summary>True when <paramref name="key"/> names a provider this build supports.</summary>
    public static bool IsKnownProvider(string key) =>
        Descriptors.Any(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a caller-supplied <c>provider</c> value to an enabled provider, or <c>null</c>.
    /// This is the check that keeps <c>/auth/challenge</c> from issuing a challenge against a
    /// scheme that was never registered, which would otherwise be an unhandled 500.
    /// </summary>
    public AuthProvider? Resolve(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : _byKey.GetValueOrDefault(key);
}
