using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

/// <summary>
/// <see cref="McpInstructionsCap"/> (#614): a downstream server's instructions are inlined whole
/// up to the cap, and past it cut on a line boundary with an explicit marker, never silently.
/// </summary>
[TestClass]
public class McpInstructionsCapTests
{
    private static string Lines(int count, int width = 40) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"L{i:D3} " + new string('x', width)));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Short instructions.")]
    public void WithinTheCap_IsUnchanged(string? instructions)
    {
        Assert.AreEqual(instructions, McpInstructionsCap.Apply(instructions, 100, ["get_guide"]));
    }

    [TestMethod]
    public void ExactlyAtTheCap_IsUnchanged()
    {
        var text = new string('a', 100);
        Assert.AreEqual(text, McpInstructionsCap.Apply(text, 100, []));
    }

    [TestMethod]
    public void OverTheCap_CutsOnALineBoundary_AndSaysHowMuchWasShown()
    {
        var text = Lines(50);  // 45 chars per line plus the break

        var capped = McpInstructionsCap.Apply(text, 500, [])!;

        var (kept, marker) = Split(capped);
        Assert.IsTrue(kept.Length <= 500);
        Assert.IsTrue(text.StartsWith(kept + "\n"), "the kept text ends at a line break of the original");
        Assert.AreEqual($"[Server instructions truncated: {kept.Length} of {text.Length} characters shown.]", marker);
    }

    [TestMethod]
    public void OneLongLine_IsCutAtTheCap()
    {
        var text = new string('a', 300);

        var (kept, marker) = Split(McpInstructionsCap.Apply(text, 100, [])!);

        Assert.AreEqual(100, kept.Length);
        StringAssert.Contains(marker, "100 of 300 characters shown");
    }

    [TestMethod]
    public void GuideTools_AreNamedInTheMarker()
    {
        var (_, marker) = Split(McpInstructionsCap.Apply(Lines(50), 500, ["`adjutant__get_guide`"])!);

        Assert.IsTrue(marker.EndsWith(" The server's own guide may cover the rest: `adjutant__get_guide`.]"), marker);
    }

    [TestMethod]
    public void GuideTools_MatchesGuideLikeNamesOnly()
    {
        CollectionAssert.AreEqual(
            new[] { "get_guide", "Get_Skill", "help", "server_instructions" },
            McpInstructionsCap.GuideTools(["send_email", "get_guide", "Get_Skill", "list_events", "help", "server_instructions"]).ToArray());
    }

    private static (string Kept, string Marker) Split(string capped)
    {
        var at = capped.LastIndexOf("\n\n[", StringComparison.Ordinal);
        Assert.IsTrue(at > 0, "no truncation marker");
        return (capped[..at], capped[(at + 2)..]);
    }
}
