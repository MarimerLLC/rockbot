namespace RockBot.Host;

/// <summary>
/// Tools that bound their own output and announce, in the result itself, how to fetch
/// the rest. The generic per-result cap, chunking, and stash machinery must leave their
/// results alone.
///
/// <para><b>Why.</b> <c>file_read</c> is the tool a model calls immediately before
/// editing a file. When the generic cap turned an 11.5 KB Slidev deck into a head + elision
/// marker + tail (issue #664), the model never fetched the stash. It called
/// <c>file_write</c> with a reconstruction of the parts it had seen and destroyed the deck.
/// <c>file_read</c> now pages on whole-line boundaries under its own, much larger budget,
/// and every partial page names the next offset. A head+tail stitch on top of that would
/// hide the paging footer, which is the one instruction that matters.</para>
///
/// <para><b>Watermark trim.</b> Unlike <see cref="StashExemptTools"/>, these results stay
/// eligible for the global context-watermark trim (<see cref="ToolResultTrimmer"/>). That
/// trim is the backstop against an unbounded context. It is deprioritised there: it
/// reclaims space from every other tool result first, so a file the model has just read
/// is not elided on the very next round-trip.</para>
/// </summary>
internal static class SelfPagingTools
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_read",
    };

    /// <summary>
    /// True when <paramref name="toolName"/> pages its own output, so its results must
    /// not be capped, chunked, or stashed per call.
    /// </summary>
    public static bool Contains(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && Names.Contains(toolName);
}
