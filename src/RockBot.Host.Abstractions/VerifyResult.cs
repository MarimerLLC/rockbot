namespace RockBot.Host;

/// <summary>
/// Outcome of running a <see cref="VerifyShape"/> against the live system.
/// </summary>
/// <param name="Outcome">Categorical outcome — drives whether the underlying claim is evicted, retained, or annotated.</param>
/// <param name="Detail">Optional diagnostic detail (error message, recovery trail) for logging and uncertainty annotations.</param>
/// <param name="TimedOut">
/// Set when <see cref="Outcome"/> is <see cref="VerifyOutcome.Uncertain"/> specifically because the
/// verifier exhausted its per-call wallclock budget. Lets the caller distinguish "tool is too slow"
/// from "executor missing" or "gateway error" so it can apply targeted retries (e.g. budget backoff)
/// rather than retrying every uncertain cause the same way.
/// </param>
public sealed record VerifyResult(
    VerifyOutcome Outcome,
    string? Detail = null,
    bool TimedOut = false);
