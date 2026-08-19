namespace RockBot.Host;

/// <summary>
/// The names of the three recall tools and the one-line scope discriminators they use to
/// describe themselves and point at each other.
/// </summary>
/// <remarks>
/// <para>
/// The tools are split across assemblies — <c>search_memory</c> and
/// <c>search_working_memory</c> live in RockBot.Memory, <c>search_conversation_history</c> in
/// RockBot.Host — but their descriptions have to read as one family, and each one names the
/// other two. Centralising the vocabulary here (an assembly both can see) is what keeps a
/// rename or a re-wording from silently desynchronising the set.
/// </para>
/// <para>
/// The discriminator is deliberately <b>what the caller is after</b>, not where it is stored:
/// concluded / returned / said. A model choosing between these tools knows what it wants to
/// find and does not know which subsystem persisted it.
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

    /// <summary>Conversation turns outside the context window. Registered by <c>ConversationRecallTools</c>.</summary>
    public const string ConversationHistory = "search_conversation_history";

    /// <summary>Lead line for <see cref="DurableMemory"/>.</summary>
    public const string DurableHeadline = "RECALL WHAT YOU CONCLUDED";

    /// <summary>Lead line for <see cref="WorkingMemory"/>.</summary>
    public const string WorkingHeadline = "RECALL WHAT A TOOL RETURNED";

    /// <summary>Lead line for <see cref="ConversationHistory"/>.</summary>
    public const string ConversationHeadline = "RECALL WHAT WAS SAID";

    /// <summary>What <see cref="DurableMemory"/> holds, phrased as the thing being looked for.</summary>
    public const string DurableScope =
        "durable facts and preferences you CONCLUDED and chose to keep";

    /// <summary>What <see cref="WorkingMemory"/> holds, phrased as the thing being looked for.</summary>
    public const string WorkingScope =
        "cached payloads a TOOL RETURNED earlier this session";

    /// <summary>What <see cref="ConversationHistory"/> holds, phrased as the thing being looked for.</summary>
    public const string ConversationScope =
        "the verbatim text of what was SAID in turns that scrolled out of your context window";

    /// <summary>Pointer to <see cref="DurableMemory"/>, for the other tools' descriptions.</summary>
    public const string TryDurable = $"for {DurableScope} use {DurableMemory}";

    /// <summary>Pointer to <see cref="WorkingMemory"/>, for the other tools' descriptions.</summary>
    public const string TryWorking = $"for {WorkingScope} use {WorkingMemory}";

    /// <summary>Pointer to <see cref="ConversationHistory"/>, for the other tools' descriptions.</summary>
    public const string TryConversation = $"for {ConversationScope} use {ConversationHistory}";

    /// <summary>
    /// Renders the "look elsewhere" line appended to an empty result, naming the two recall
    /// tools other than <paramref name="callingTool"/>.
    /// </summary>
    /// <remarks>
    /// An empty result is the moment a mis-routed recall attempt either recovers or turns into
    /// the agent concluding it never knew something. Without this the three tools are three
    /// dead ends, and the model's only signal that it picked the wrong one is silence — which
    /// reads identically to "this was never said."
    /// </remarks>
    /// <param name="callingTool">
    /// The tool rendering the message; omitted from the suggestions. Pass one of
    /// <see cref="DurableMemory"/>, <see cref="WorkingMemory"/>, or
    /// <see cref="ConversationHistory"/>.
    /// </param>
    public static string LookElsewhere(string callingTool)
    {
        var others = new List<string>(2);

        if (callingTool != DurableMemory) others.Add(TryDurable);
        if (callingTool != WorkingMemory) others.Add(TryWorking);
        if (callingTool != ConversationHistory) others.Add(TryConversation);

        return $"This searched only {ScopeOf(callingTool)}. Not finding it here is not evidence " +
               $"it was never said or never known — {string.Join("; ", others)}.";
    }

    private static string ScopeOf(string tool) => tool switch
    {
        DurableMemory => DurableScope,
        WorkingMemory => WorkingScope,
        ConversationHistory => ConversationScope,
        _ => "one recall store"
    };
}
