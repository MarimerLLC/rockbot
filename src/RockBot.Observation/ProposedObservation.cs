namespace RockBot.Observation;

/// <summary>
/// Raw observation proposed by the extraction LLM, before quote-grounding
/// validation. Each proposal must cite a specific turn with a verbatim
/// (or near-verbatim, whitespace-normalised) quote; observations whose
/// claimed quote is not present in the source transcript are discarded
/// before they enter the candidate pool.
/// </summary>
/// <param name="Text">Canonical text of the observation (the LLM's wording).</param>
/// <param name="ConversationId">Conversation the supporting evidence is from.</param>
/// <param name="TurnId">Specific turn within the conversation that supports the claim.</param>
/// <param name="Quote">Verbatim snippet from the cited turn that supports the claim. Whitespace-normalised matching is allowed; the quote must be substring-present in the cited turn after normalisation.</param>
public sealed record ProposedObservation(
    string Text,
    string ConversationId,
    string TurnId,
    string Quote);
