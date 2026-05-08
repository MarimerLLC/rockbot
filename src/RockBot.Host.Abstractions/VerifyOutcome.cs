using System.Text.Json.Serialization;

namespace RockBot.Host;

/// <summary>Categorical outcome of evaluating a <see cref="VerifyShape"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerifyOutcome>))]
public enum VerifyOutcome
{
    /// <summary>Predicate succeeded — observed behaviour falsifies the claim. Entry should be evicted from memory and skipped.</summary>
    PredicateSucceeded,

    /// <summary>Predicate failed — observed behaviour is consistent with the claim. Entry should be injected as before.</summary>
    PredicateFailed,

    /// <summary>Predicate could not be evaluated within budget (timeout or gateway error unrelated to the predicate). Entry should be injected with a verifier-uncertain annotation.</summary>
    Uncertain
}
