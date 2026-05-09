namespace RockBot.Host;

/// <summary>
/// Evaluates a <see cref="VerifyShape"/> against the live system to decide whether
/// a <see cref="RepairTicket"/>'s applied <c>Change</c> resolved the underlying
/// failure cluster. Repair-ticket verification is uncached — every attempt must
/// observe post-apply state, never a previous cycle's result.
/// </summary>
public interface IRepairTicketVerifier
{
    /// <summary>Evaluates the verify shape with the implementation's default budget.</summary>
    Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the verify shape with an explicit wallclock budget. The apply pass uses
    /// this overload to apply backoff after repeated timeouts on slow verify shapes
    /// (e.g. tools that fan out across accounts). When <paramref name="budget"/> is null,
    /// the implementation's default budget is used.
    /// </summary>
    Task<VerifyResult> VerifyAsync(VerifyShape shape, TimeSpan? budget, CancellationToken cancellationToken = default) =>
        VerifyAsync(shape, cancellationToken);
}
