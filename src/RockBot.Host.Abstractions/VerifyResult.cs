namespace RockBot.Host;

/// <summary>
/// Outcome of running a <see cref="VerifyShape"/> against the live system.
/// </summary>
/// <param name="Outcome">Categorical outcome — drives whether the underlying claim is evicted, retained, or annotated.</param>
/// <param name="Detail">Optional diagnostic detail (error message, recovery trail) for logging and uncertainty annotations.</param>
public sealed record VerifyResult(
    VerifyOutcome Outcome,
    string? Detail = null);
