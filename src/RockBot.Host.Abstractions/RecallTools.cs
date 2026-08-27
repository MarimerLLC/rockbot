namespace RockBot.Host;

/// <summary>
/// The names of the recall tools and the one-line scope discriminators they use to describe
/// themselves and point at each other.
/// </summary>
/// <remarks>
/// <para>
/// The tools are split across assemblies — <c>search_memory</c> and
/// <c>search_working_memory</c> both live in RockBot.Memory today, but their descriptions have
/// to read as one family and each one names the others. Centralising the vocabulary here (an
/// assembly every registrar can see) is what keeps a rename or a re-wording from silently
/// desynchronising the set.
/// </para>
/// <para>
/// The discriminator is deliberately <b>what the caller is after</b>, not where it is stored:
/// concluded / returned. A model choosing between these tools knows what it wants to find and
/// does not know which subsystem persisted it.
/// </para>
/// <para>
/// The family is sized to hold a third member. A recall path for conversation turns that fall
/// outside the context window (<c>search_conversation_history</c>) is tracked by #509 and
/// blocked on #530 — adding it back means a name, a headline, a scope, a <c>Try…</c> pointer,
/// and one arm on <see cref="ScopeOf"/>. <see cref="LookElsewhere"/> needs no change.
/// </para>
/// <para>
/// Every member is <c>const</c> because these are used inside <c>[Description]</c> attributes,
/// whose arguments must be compile-time constants.
/// </para>
/// </remarks>
public static class RecallTools
{
    /// <summary>Durable cross-session knowledge. Registered by <c>MemoryTools</c>.</summary>
    public const string DurableMemory = "search_memory";

    /// <summary>This session's ephemeral cached payloads. Registered by <c>WorkingMemoryTools</c>.</summary>
    public const string WorkingMemory = "search_working_memory";

    /// <summary>Lead line for <see cref="DurableMemory"/>.</summary>
    public const string DurableHeadline = "RECALL WHAT YOU CONCLUDED";

    /// <summary>Lead line for <see cref="WorkingMemory"/>.</summary>
    public const string WorkingHeadline = "RECALL WHAT A TOOL RETURNED";

    /// <summary>What <see cref="DurableMemory"/> holds, phrased as the thing being looked for.</summary>
    public const string DurableScope =
        "durable facts and preferences you CONCLUDED and chose to keep";

    /// <summary>What <see cref="WorkingMemory"/> holds, phrased as the thing being looked for.</summary>
    public const string WorkingScope =
        "cached payloads a TOOL RETURNED earlier this session";

    /// <summary>Pointer to <see cref="DurableMemory"/>, for the other tools' descriptions.</summary>
    public const string TryDurable = $"for {DurableScope} use {DurableMemory}";

    /// <summary>Pointer to <see cref="WorkingMemory"/>, for the other tools' descriptions.</summary>
    public const string TryWorking = $"for {WorkingScope} use {WorkingMemory}";

    /// <summary>
    /// Renders the "look elsewhere" line appended to an empty result, naming the recall tools
    /// other than <paramref name="callingTool"/>.
    /// </summary>
    /// <remarks>
    /// An empty result is the moment a mis-routed recall attempt either recovers or turns into
    /// the agent concluding it never knew something. Without this the tools are dead ends, and
    /// the model's only signal that it picked the wrong one is silence — which reads
    /// identically to "this was never said."
    /// </remarks>
    /// <param name="callingTool">
    /// The tool rendering the message; omitted from the suggestions. Pass one of
    /// <see cref="DurableMemory"/> or <see cref="WorkingMemory"/>.
    /// </param>
    public static string LookElsewhere(string callingTool)
    {
        var others = new List<string>(1);

        if (callingTool != DurableMemory) others.Add(TryDurable);
        if (callingTool != WorkingMemory) others.Add(TryWorking);

        return $"This searched only {ScopeOf(callingTool)}. Not finding it here is not evidence " +
               $"it was never said or never known — {string.Join("; ", others)}.";
    }

    private static string ScopeOf(string tool) => tool switch
    {
        DurableMemory => DurableScope,
        WorkingMemory => WorkingScope,
        _ => "one recall store"
    };
}
