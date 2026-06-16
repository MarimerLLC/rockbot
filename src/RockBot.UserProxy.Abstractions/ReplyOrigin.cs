namespace RockBot.UserProxy;

/// <summary>
/// Provenance for an <b>unsolicited</b> agent reply — one produced without an immediately
/// preceding user turn in the receiving client (a subagent completion, scheduled task,
/// A2A result, idle inbox batch). Lets a frontend render a short anchor preamble so the
/// user can see what the message is about, when the work started, and where it came from.
/// </summary>
/// <param name="Channel">Originating channel, e.g. "cli", "blazor", "discord", "scheduled", "a2a-inbound".</param>
/// <param name="PromptSummary">Truncated (or summarized) first user turn that started the work.</param>
/// <param name="StartedAt">When the originating request started.</param>
/// <param name="SessionId">Original session id, for deep-linking / dedupe / anchor suppression.</param>
public sealed record ReplyOrigin(
    string Channel,
    string PromptSummary,
    DateTimeOffset StartedAt,
    string? SessionId);
