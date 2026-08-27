using Microsoft.AspNetCore.DataProtection;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Points the ASP.NET Core data-protection key ring at a persistent directory.
/// </summary>
/// <remarks>
/// Without this the key ring is generated in memory and discarded when the process exits, so every
/// payload protected by it — antiforgery tokens today, authentication cookies once sign-in exists —
/// is invalidated on restart. In a container that means every pod restart or rollout.
/// </remarks>
public static class DataProtectionSetup
{
    /// <summary>
    /// Application name (the data-protection purpose root) for the Blazor UI. Pinned rather than
    /// derived: the default is a hash of the content root path, so changing <c>WORKDIR</c> in the
    /// Dockerfile would silently invalidate every payload the existing key ring had protected.
    /// </summary>
    public const string ApplicationName = "rockbot-blazor";

    /// <summary>Configuration key holding the key-ring directory. Empty means "use ASP.NET defaults".</summary>
    public const string KeyRingPathKey = "DataProtection:KeyRingPath";

    /// <summary>
    /// Persists the data-protection key ring to <c>DataProtection:KeyRingPath</c> when that setting
    /// is present. When it is empty the ASP.NET Core defaults are left alone — on a developer
    /// machine those already resolve to a persistent per-user profile directory, so only container
    /// deployments need to set the path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configured directory cannot be created or written to. This throws rather than falling
    /// back because the fallback is in-memory keys: the app would start, serve traffic, and lose
    /// every session on restart with nothing in the logs pointing at the mount.
    /// </exception>
    public static IServiceCollection AddRockBotDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var keyRingPath = configuration[KeyRingPathKey];
        if (string.IsNullOrWhiteSpace(keyRingPath))
            return services;

        var directory = EnsureWritable(keyRingPath.Trim());

        services.AddDataProtection()
            .PersistKeysToFileSystem(directory)
            .SetApplicationName(ApplicationName);

        return services;
    }

    /// <summary>
    /// Creates the key-ring directory if needed and proves it is writable by round-tripping a probe
    /// file. Retired keys are never pruned — they are still required to decrypt payloads protected
    /// before the last ~90-day rollover.
    /// </summary>
    private static DirectoryInfo EnsureWritable(string keyRingPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(keyRingPath);
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Data protection key ring directory '{keyRingPath}' could not be created: {ex.Message}. " +
                "Check that the volume is mounted read-write and that the container user can write to it.", ex);
        }

        var probe = Path.Combine(fullPath, $".rockbot-write-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Data protection key ring directory '{fullPath}' is not writable: {ex.Message}. " +
                "Check that the volume is mounted read-write and that the container user can write to it.", ex);
        }

        return new DirectoryInfo(fullPath);
    }
}
