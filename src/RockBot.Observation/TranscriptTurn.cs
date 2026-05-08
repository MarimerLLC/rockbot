namespace RockBot.Observation;

/// <summary>
/// One turn of conversation surfaced to the observation framework. The framework
/// is decoupled from the host's conversation log shape: an adapter converts
/// host-side conversation records into <see cref="TranscriptTurn"/> instances
/// before passing them to <see cref="ITranscriptFilter"/> and the extraction
/// pipeline.
/// </summary>
/// <param name="ConversationId">Conversation the turn belongs to.</param>
/// <param name="TurnId">Turn ID within the conversation.</param>
/// <param name="Source">Origin of the turn — "user", "agent", "scheduled-task", "heartbeat", etc. Filters use this to scope what they extract from.</param>
/// <param name="Role">Chat role for this turn ("user", "assistant", "system", "tool"). Distinct from <see cref="Source"/>: an agent-authored "assistant" turn during a scheduled-task run has Source="scheduled-task" and Role="assistant".</param>
/// <param name="Content">Verbatim turn text. Tool calls and structured payloads are not modelled here; targets that care about behavior trajectory (theory-of-self) supplement this with structured behavior summaries computed elsewhere.</param>
/// <param name="Timestamp">When the turn occurred.</param>
public sealed record TranscriptTurn(
    string ConversationId,
    string TurnId,
    string Source,
    string Role,
    string Content,
    DateTimeOffset Timestamp);
