namespace RockBot.Host;

/// <summary>
/// Plain-language explanations for the memory audit's invariant names.
/// </summary>
/// <remarks>
/// <para>
/// <c>chain-depth-threshold</c> is jargon. It is precise, it is stable, and it is the right
/// thing to key a lookup on — and it tells a person nothing. The audit's whole purpose is that
/// somebody can act on what it finds, so every finding has to be able to explain itself in
/// words that assume no knowledge of how memory consolidation works.
/// </para>
/// <para>
/// This lives here, in the shared abstractions, because the two things that surface findings sit
/// on opposite sides of the process boundary: the host writes the report, and the introspection
/// sidecar answers <c>get_memory_audit</c>. A glossary that lived in only one of them would let
/// the other keep emitting bare identifiers.
/// </para>
/// <para>
/// Applied at read time, never stored. The explanations are static text; writing them into every
/// snapshot row would multiply the trend file's size by a large constant to say the same thing
/// on every line.
/// </para>
/// </remarks>
public static class MemoryAuditGlossary
{
    /// <summary>What one invariant means, for someone who has never read the code.</summary>
    /// <param name="Title">A short plain-language name for the finding.</param>
    /// <param name="WhatItMeans">What actually happened, in ordinary words.</param>
    /// <param name="WhatToDo">What a person should do about it, including "nothing urgent".</param>
    /// <param name="Severity">
    /// <c>alert</c> when something was destroyed, <c>warning</c> when a limit was crossed but
    /// nothing was lost.
    /// </param>
    public sealed record Definition(
        string Title,
        string WhatItMeans,
        string WhatToDo,
        string Severity);

    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["no-hard-delete-outside-purge"] = new(
            "Memories disappeared unexpectedly",
            "Some memories are simply gone from disk, and the normal clean-up process cannot account " +
            "for them. Old memories are supposed to be retired first and only deleted months later; " +
            "these skipped that path entirely. This is the most serious thing the audit checks for.",
            "Treat this as a possible data-loss incident. The report lists the affected memory ids. " +
            "Check whether a backup from before the run still holds them, and look at what ran " +
            "between the two audit runs. If it recurs, memory consolidation can be paused.",
            MemoryAuditStatuses.Alert),

        ["loss-percent-threshold"] = new(
            "The memory store shrank sharply",
            "The number of active memories dropped by more than the allowed percentage since the " +
            "previous check. Memory should shrink slowly as duplicates are merged away, not in steps.",
            "Look at what ran in between — a consolidation pass that merged very aggressively is the " +
            "usual cause. The merged-away originals are kept for months, so the content is normally " +
            "still recoverable.",
            MemoryAuditStatuses.Alert),

        ["merge-chain-unbroken"] = new(
            "A merged memory's replacement is missing",
            "When two memories are combined, the originals are retired and marked \"merged into\" the " +
            "new combined one. Here the originals point at a combined memory that does not exist. " +
            "The information in them has no surviving copy anywhere.",
            "The affected ids are in the report and their content is still readable on disk. This " +
            "indicates the merge did not finish, so it is worth checking whether more than the " +
            "listed entries were affected.",
            MemoryAuditStatuses.Alert),

        ["chain-depth-threshold"] = new(
            "A memory has been rewritten too many times",
            "Memories that say similar things get combined into one. That combined memory can later " +
            "be combined again, and again. This finding means some memory is now several generations " +
            "deep — a summary of a summary of a summary. Nothing has been lost that the checks can " +
            "detect, but each rewrite is another chance for a detail or a nuance to quietly drift.",
            "Not urgent. Worth spot-checking the deepest memory against what it originally said — the " +
            "originals are kept. If the depth keeps climbing, memories are being merged more often " +
            "than the information really changes.",
            MemoryAuditStatuses.Warning),

        ["net-growth-threshold"] = new(
            "Memory is growing faster than it is being tidied",
            "New memories are being saved faster than duplicates are being merged away, so the store " +
            "is getting bigger over time rather than settling. Nothing is wrong with any individual " +
            "memory; there are just more of them each day.",
            "Not urgent. If it persists for weeks, either the agent is saving too eagerly or " +
            "consolidation is not running often enough.",
            MemoryAuditStatuses.Warning),

        ["no-repeated-rejection"] = new(
            "The same merge keeps being attempted and refused",
            "A safety check refuses to combine memories when the combined version would drop a name, " +
            "date or number. The same group of memories has now been proposed and refused several " +
            "times in a row, so effort is being spent on work that never completes.",
            "Not urgent, and the refusal is the safety net working. The affected memories may need " +
            "editing by hand, or they may simply be distinct facts that should never be combined.",
            MemoryAuditStatuses.Warning),

        ["rejected-merges-threshold"] = new(
            "Unusually many merges are being refused",
            "The safety check that stops a merge from dropping specifics is firing more often than " +
            "expected. It means merges are being attempted that would have lost information — and " +
            "that they were caught.",
            "Not urgent. A sustained rise suggests memories are being grouped for merging too " +
            "loosely.",
            MemoryAuditStatuses.Warning),

        ["merged-from-resolves"] = new(
            "A recent merge refers to originals that are missing",
            "A recently combined memory records which memories it came from, and some of those are no " +
            "longer on disk. Old originals are deleted by design after several months; these are too " +
            "recent for that to be the explanation.",
            "Worth a look. The combined memory itself is intact — what is lost is the ability to " +
            "check it against what it replaced.",
            MemoryAuditStatuses.Warning),

        ["live-not-merge-source"] = new(
            "A memory that was merged away is still active",
            "A memory was combined into a new one, but the original was never retired, so both are " +
            "still in use. Searches can now surface the same fact twice.",
            "Not urgent and not a loss — the opposite, a duplicate. It will usually be cleaned up by " +
            "the next consolidation pass.",
            MemoryAuditStatuses.Warning),

        ["archive-fields-present"] = new(
            "A retired memory is missing its explanation",
            "When a memory is retired, the system records both when and why. On these entries one of " +
            "the two is missing, so there is no record of why it left active use.",
            "Not urgent. The memory's content is intact; only the audit trail is incomplete.",
            MemoryAuditStatuses.Warning),

        ["no-malformed-files"] = new(
            "Some memory files could not be read",
            "Files in the memory folder could not be understood as memories and were skipped. They " +
            "are usually truncated by an interrupted write, and they are invisible to the agent.",
            "Worth a look, because a skipped file is a memory the agent cannot use. The file is still " +
            "on disk and may be repairable by hand.",
            MemoryAuditStatuses.Warning)
    };

    /// <summary>
    /// The explanation for <paramref name="invariantName"/>, or <c>null</c> for a name this
    /// glossary does not know.
    /// </summary>
    /// <remarks>
    /// Returning null rather than a generic filler is deliberate: a new invariant added without a
    /// definition should be visibly missing one, not quietly described as "a memory-health check
    /// failed".
    /// </remarks>
    public static Definition? Describe(string invariantName) =>
        Definitions.GetValueOrDefault(invariantName);

    /// <summary>Every definition, for building a guide document.</summary>
    public static IReadOnlyDictionary<string, Definition> All => Definitions;
}
