using System.Text.Json;

namespace RockBot.Host;

/// <summary>
/// One apply+verify attempt against a <see cref="RepairTicket"/>. The ticket
/// retains the full attempt history so escalation summaries and dedup logic
/// can introspect prior tries.
/// </summary>
/// <param name="At">UTC timestamp of the attempt.</param>
/// <param name="AppliedDiff">Structured diff describing what the applier did. Shape depends on the target.</param>
/// <param name="Result">Outcome of the post-apply <see cref="VerifyShape"/> evaluation.</param>
public sealed record RepairAttempt(
    DateTimeOffset At,
    JsonElement AppliedDiff,
    VerifyResult Result);
