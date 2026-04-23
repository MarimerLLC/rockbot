namespace RockBot.Host.Tests;

[TestClass]
public class LeakedToolSyntaxRegexTests
{
    // ── LeakedToolSyntaxRegex — specific OpenAI scaffolding leaks ─────────────

    [TestMethod]
    public void LeakedToolSyntax_Matches_MultiToolUseParallel()
    {
        Assert.IsTrue(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch(
            "sure, let me help\nto=multi_tool_use.parallel\n{\"tool_uses\":[]}"));
    }

    [TestMethod]
    public void LeakedToolSyntax_Matches_MultiToolUseParallel_WithExtraSpaces()
    {
        Assert.IsTrue(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch(
            "▶\nSubagent\nto = multi_tool_use.parallel extra text"));
    }

    [TestMethod]
    public void LeakedToolSyntax_Matches_FunctionsScaffoldingLeak()
    {
        Assert.IsTrue(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch(
            "calling to=functions.spawn_subagent with args"));
    }

    [TestMethod]
    public void LeakedToolSyntax_DoesNotMatch_PlainEnglish()
    {
        Assert.IsFalse(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch(
            "I scheduled the meeting for 3pm tomorrow. Let me know if you need anything else."));
    }

    [TestMethod]
    public void LeakedToolSyntax_DoesNotMatch_ProseWithToAndFunctions()
    {
        // Regular tool-result prose that mentions "to" and "functions" separately.
        Assert.IsFalse(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch(
            "The function returned a value. I will forward it to the next step."));
    }

    [TestMethod]
    public void LeakedToolSyntax_DoesNotMatch_ChineseText()
    {
        // Tool-syntax regex is language-agnostic — Chinese alone should not trip it.
        // (That case is covered by the separate UnexpectedCjkRegex.)
        Assert.IsFalse(AgentLoopRunner.LeakedToolSyntaxRegex.IsMatch("北京赛车计划 大发游戏"));
    }

    // ── UnexpectedCjkRegex — heuristic for English-only deployments ──────────

    [TestMethod]
    public void UnexpectedCjk_Matches_GamblingSpam()
    {
        // The exact pattern observed in the wild: CJK gambling-SEO content.
        Assert.IsTrue(AgentLoopRunner.UnexpectedCjkRegex.IsMatch("北京赛车计划 大发游戏 盈立_json"));
    }

    [TestMethod]
    public void UnexpectedCjk_Matches_ThreeOrMoreConsecutiveCjk()
    {
        Assert.IsTrue(AgentLoopRunner.UnexpectedCjkRegex.IsMatch("some text 中文内容 more"));
    }

    [TestMethod]
    public void UnexpectedCjk_DoesNotMatch_SingleCjkCharacter()
    {
        // A lone kanji (e.g. from a name like "zashiki (座)") should not trip.
        Assert.IsFalse(AgentLoopRunner.UnexpectedCjkRegex.IsMatch("the character 座 means 'seat'"));
    }

    [TestMethod]
    public void UnexpectedCjk_DoesNotMatch_TwoConsecutiveCjkCharacters()
    {
        // Two in a row — e.g. 座敷 (zashiki) in the identity prompt — should not trip.
        Assert.IsFalse(AgentLoopRunner.UnexpectedCjkRegex.IsMatch(
            "in the tradition of the Japanese 座敷 household spirit"));
    }

    [TestMethod]
    public void UnexpectedCjk_DoesNotMatch_HiraganaAlone()
    {
        // Hiragana is outside the regex range by design — a long わらし string should not trip.
        Assert.IsFalse(AgentLoopRunner.UnexpectedCjkRegex.IsMatch("the word わらし means 'child'"));
    }

    [TestMethod]
    public void UnexpectedCjk_DoesNotMatch_ToolSyntaxAlone()
    {
        // CJK regex is CJK-only — tool-syntax leak alone should not trip this one.
        // (That case is covered by the separate LeakedToolSyntaxRegex.)
        Assert.IsFalse(AgentLoopRunner.UnexpectedCjkRegex.IsMatch("to=multi_tool_use.parallel"));
    }

    [TestMethod]
    public void UnexpectedCjk_DoesNotMatch_PlainEnglishMarkdown()
    {
        Assert.IsFalse(AgentLoopRunner.UnexpectedCjkRegex.IsMatch(
            "Here is a summary:\n- Item 1\n- Item 2\n\n## Next steps\nI will do X."));
    }
}
