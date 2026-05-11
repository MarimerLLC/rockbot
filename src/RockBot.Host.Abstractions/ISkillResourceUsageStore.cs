namespace RockBot.Host;

/// <summary>
/// Records when a skill resource is fetched (a "checkout") so the validation pass
/// can use access events as a soft signal for non-wisp resources whose success/failure
/// can't be cross-referenced via the wisp execution log's <c>definitionHash</c>.
///
/// Wisp resources have a stronger signal: the cross-reference between
/// <c>SkillResource.DefinitionHash</c> and <c>WispExecutionRecord.DefinitionHash</c>
/// gives a true success/failure count. Checkouts are the fallback for Python scripts,
/// schemas, and other resource types.
/// </summary>
public interface ISkillResourceUsageStore
{
    /// <summary>Records a single checkout event (fire-and-forget call site).</summary>
    Task RecordCheckoutAsync(
        string skillName,
        string filename,
        string sessionId,
        DateTimeOffset at,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all checkout events for the given resource on or after
    /// <paramref name="since"/>, ordered by timestamp ascending.
    /// </summary>
    Task<IReadOnlyList<SkillResourceCheckoutEvent>> QueryCheckoutsAsync(
        string skillName,
        string filename,
        DateTimeOffset since,
        CancellationToken ct = default);
}

/// <summary>
/// Persisted record of a single skill-resource fetch event.
/// </summary>
public sealed record SkillResourceCheckoutEvent(
    string SkillName,
    string Filename,
    string SessionId,
    DateTimeOffset Timestamp);
