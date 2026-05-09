namespace RockBot.Host;

/// <summary>
/// Evaluates a <see cref="VerifyShape"/> against the live system to decide whether
/// a <see cref="RepairTicket"/>'s applied <c>Change</c> resolved the underlying
/// failure cluster. Repair-ticket verification is uncached — every attempt must
/// observe post-apply state, never a previous cycle's result.
/// </summary>
public interface IRepairTicketVerifier
{
    Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default);
}
