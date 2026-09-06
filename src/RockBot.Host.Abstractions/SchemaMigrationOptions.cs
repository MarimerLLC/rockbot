namespace RockBot.Host;

/// <summary>
/// Options for the startup schema migration check.
/// </summary>
public sealed class SchemaMigrationOptions
{
    /// <summary>
    /// Whether the check runs at all. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Turning this off starts the host against whatever is on disk, unmigrated and unmarked.
    /// Only useful for a host that migrates its stores by some other means.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Report what would happen without touching anything. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Pending migrations are logged and skipped, and no version marker is written. Lets an
    /// operator boot a copy of a deployment to see what an upgrade would do to its data before
    /// committing to it.
    /// </remarks>
    public bool DryRun { get; set; }
}
