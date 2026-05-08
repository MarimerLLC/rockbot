namespace RockBot.Observation;

/// <summary>
/// Concrete evidence pinning a candidate or theory observation to a specific
/// place in conversation history. The combination of <see cref="ConversationId"/>,
/// <see cref="TurnId"/>, and <see cref="Quote"/> is what makes
/// "promote when seen N distinct conversations" honest — N must be N independent
/// conversations, not N paraphrases of the same one.
/// </summary>
/// <param name="ConversationId">Conversation the quote came from.</param>
/// <param name="TurnId">Turn within the conversation.</param>
/// <param name="Quote">Verbatim snippet from the source turn. Mechanically validated against the input transcript at extraction time; observations whose quote is not actually present in the source are discarded before they enter the candidate pool.</param>
/// <param name="ObservedAt">When the extraction that produced this reference ran.</param>
public sealed record ObservationReference(
    string ConversationId,
    string TurnId,
    string Quote,
    DateTimeOffset ObservedAt);
