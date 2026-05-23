namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Default <see cref="ITokenProviderRegistry"/> backed by an in-memory dictionary
/// populated from the DI container's <see cref="TokenProviderRegistration"/> bindings.
/// </summary>
public sealed class TokenProviderRegistry : ITokenProviderRegistry
{
    private readonly Dictionary<string, ITokenProvider> _providers;

    public TokenProviderRegistry(IEnumerable<TokenProviderRegistration> registrations)
    {
        _providers = registrations.ToDictionary(
            r => r.Profile,
            r => r.Provider,
            StringComparer.OrdinalIgnoreCase);
    }

    public ITokenProvider Get(string profile)
    {
        if (_providers.TryGetValue(profile, out var provider))
            return provider;

        throw new KeyNotFoundException(
            $"No token provider is registered for auth profile '{profile}'. " +
            $"Known profiles: [{string.Join(", ", _providers.Keys)}].");
    }
}
