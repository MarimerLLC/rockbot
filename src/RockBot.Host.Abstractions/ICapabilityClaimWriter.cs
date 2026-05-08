namespace RockBot.Host;

/// <summary>
/// Internal write API for capability claims. Not exposed as an LLM tool — only the MCP
/// gateway (after exhausted recovery) and the dream service (when promoting an
/// observation to a claim) call this.
/// </summary>
public interface ICapabilityClaimWriter
{
    /// <summary>
    /// Persists a capability claim in long-term memory under the conventional
    /// <c>claim/capability/{server}/{tool}</c> category.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the claim is missing required fields, lacks a verify shape, or has
    /// an inconsistent expectation (e.g. <see cref="VerifyExpectationKind.FailureWithMessage"/>
    /// without a <see cref="VerifyExpectation.FailurePattern"/>).
    /// </exception>
    Task SaveCapabilityClaimAsync(CapabilityClaim claim, CancellationToken cancellationToken = default);
}
