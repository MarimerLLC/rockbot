namespace RockBot.Host;

/// <summary>
/// Enrols one persisted store in the startup migration check: what it is called, what schema
/// version the running code expects, and where its data lives.
/// </summary>
/// <remarks>
/// Registered as a singleton by whichever extension method registers the store itself, so a
/// consumer that never opts into a store never gets a version marker for it. Third-party
/// stores enrol the same way — see <c>AddStoreSchema</c> in <c>RockBot.Host</c>.
/// </remarks>
public sealed class StoreSchemaDescriptor
{
    /// <summary>
    /// Creates a descriptor for a store.
    /// </summary>
    /// <param name="storeName">
    /// Stable name for the store, recorded in its version marker. Changing it after release
    /// orphans existing markers, so treat it as part of the on-disk format.
    /// </param>
    /// <param name="currentVersion">The schema version the running code expects. Must be at least 1.</param>
    /// <param name="resolvePath">
    /// Resolves the store's root directory from the service provider. Deferred rather than a
    /// plain string because store paths come from bound options that are not available at
    /// registration time.
    /// </param>
    /// <param name="legacyVersion">
    /// The version to assume for a store that already holds data but carries no marker —
    /// a deployment that predates this mechanism. Defaults to 1.
    /// </param>
    public StoreSchemaDescriptor(
        string storeName,
        int currentVersion,
        Func<IServiceProvider, string> resolvePath,
        int legacyVersion = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(legacyVersion, 1);
        ArgumentNullException.ThrowIfNull(resolvePath);

        StoreName = storeName;
        CurrentVersion = currentVersion;
        ResolvePath = resolvePath;
        LegacyVersion = legacyVersion;
    }

    /// <summary>Stable name for the store, recorded in its version marker.</summary>
    public string StoreName { get; }

    /// <summary>The schema version the running code expects.</summary>
    public int CurrentVersion { get; }

    /// <summary>Resolves the store's root directory from the service provider.</summary>
    public Func<IServiceProvider, string> ResolvePath { get; }

    /// <summary>The version assumed for an unmarked store that already holds data.</summary>
    public int LegacyVersion { get; }
}
