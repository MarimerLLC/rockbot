namespace RockBot.Host;

/// <summary>
/// Well-known long-term memory category names for capability claims (see
/// <see cref="CapabilityClaim"/>). Entries under these categories carry a
/// <see cref="VerifyShape"/> on <see cref="MemoryEntry.Verify"/> and are subject to
/// read-side falsification before injection.
/// </summary>
public static class CapabilityClaimCategories
{
    /// <summary>Category prefix for all capability-claim entries.</summary>
    public const string Prefix = "claim/capability";

    /// <summary>
    /// Builds the full per-tool category path: <c>claim/capability/{server}/{tool}</c>.
    /// </summary>
    public static string For(string server, string tool) =>
        $"{Prefix}/{server}/{tool}";

    /// <summary>
    /// Returns <c>true</c> when the given category names a capability claim.
    /// Accepts <c>null</c> and returns <c>false</c>.
    /// </summary>
    public static bool IsCapabilityClaim(string? category) =>
        category is not null
        && (category.Equals(Prefix, StringComparison.Ordinal)
            || category.StartsWith(Prefix + "/", StringComparison.Ordinal));
}
