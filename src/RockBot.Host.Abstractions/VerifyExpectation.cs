namespace RockBot.Host;

/// <summary>
/// What outcome a <see cref="VerifyShape"/> expects when the claim is wrong (i.e. the
/// predicate succeeds and the underlying capability claim should be evicted).
/// </summary>
/// <param name="Kind">The kind of outcome that constitutes predicate success.</param>
/// <param name="FailurePattern">
/// Required when <see cref="Kind"/> is <see cref="VerifyExpectationKind.FailureWithMessage"/>.
/// Substring (case-insensitive) matched against the verify call's error message.
/// Ignored when <see cref="Kind"/> is <see cref="VerifyExpectationKind.Success"/>.
/// </param>
public sealed record VerifyExpectation(
    VerifyExpectationKind Kind,
    string? FailurePattern = null);
