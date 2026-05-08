namespace RockBot.Host;

/// <summary>
/// Evaluates a <see cref="VerifyShape"/> against the live system. Implementations cache
/// results per-process so a shape used many times in a session does not pay the
/// gateway cost on every call.
/// </summary>
public interface ICapabilityClaimVerifier
{
    /// <summary>
    /// Returns a categorical outcome. Never throws on predicate evaluation; gateway
    /// errors and budget exhaustion are reported as <see cref="VerifyOutcome.Uncertain"/>.
    /// May propagate <see cref="OperationCanceledException"/> when the caller's token is cancelled.
    /// </summary>
    Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken ct = default);
}
