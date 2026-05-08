using System.Text.Json.Serialization;

namespace RockBot.Host;

/// <summary>Discriminator for <see cref="VerifyExpectation"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerifyExpectationKind>))]
public enum VerifyExpectationKind
{
    /// <summary>Predicate succeeds when the verify call returns without error.
    /// Use when the underlying claim asserts the call always fails.</summary>
    Success,

    /// <summary>Predicate succeeds when the verify call fails with an error message
    /// containing <see cref="VerifyExpectation.FailurePattern"/> (case-insensitive
    /// substring). Use when the claim asserts the call always succeeds.</summary>
    FailureWithMessage
}
