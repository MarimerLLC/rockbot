using RockBot.UserProxy;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class ClientCapabilityPromptBuilderTests
{
    [TestMethod]
    public void Build_None_ReturnsNull()
    {
        Assert.IsNull(ClientCapabilityPromptBuilder.Build(ClientCapabilities.None));
    }

    [TestMethod]
    public void Build_TextOnly_ReturnsNull()
    {
        // Text alone is the implicit floor — no opt-in beyond plain text, so the
        // builder returns null and AgentContextBuilder skips prompt injection.
        Assert.IsNull(ClientCapabilityPromptBuilder.Build(ClientCapabilities.Text));
    }

    [TestMethod]
    public void Build_OnlyUnknownNativeUi_DoesNotReturnNull()
    {
        // Reserved native-UI bits still count as a meaningful opt-in even though no
        // emitter tooling exists for them yet — the prompt acknowledges them.
        var snippet = ClientCapabilityPromptBuilder.Build(
            ClientCapabilities.Text | ClientCapabilities.DiscordEmbed);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "platform-native UI primitives");
    }

    [TestMethod]
    public void Build_CliPreset_AllowsBasicAndCode_DeniesHeadingsTablesLinks()
    {
        var snippet = ClientCapabilityPromptBuilder.Build(ClientCapabilityPresets.Cli);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "bold");
        StringAssert.Contains(snippet, "fenced code blocks");
        // CLI preset omits headings/tables/inline-link — the deny list should call them out.
        StringAssert.Contains(snippet, "headings");
        StringAssert.Contains(snippet, "tables");
        StringAssert.Contains(snippet, "auto-link");
        // No HTML / SVG mention for CLI
        Assert.IsFalse(snippet.Contains("inline HTML"), "CLI must not be told it can emit inline HTML");
        Assert.IsFalse(snippet.Contains("inline `<svg>`"), "CLI must not be told it can emit inline SVG");
    }

    [TestMethod]
    public void Build_BlazorPreset_AllowsRichSet_NoConflictingDeny()
    {
        var snippet = ClientCapabilityPromptBuilder.Build(ClientCapabilityPresets.Blazor);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "bold");
        StringAssert.Contains(snippet, "headings");
        StringAssert.Contains(snippet, "GFM-style tables");
        StringAssert.Contains(snippet, "fenced code blocks");
        StringAssert.Contains(snippet, "inline links");
        StringAssert.Contains(snippet, "strikethrough");
        StringAssert.Contains(snippet, "task-list");
        StringAssert.Contains(snippet, "inline HTML");
        StringAssert.Contains(snippet, "inline `<svg>`");
        // Blazor allows everything in this subset — no markdown-feature deny lines should appear.
        // Only the HTML safety deny line should be present.
        Assert.IsFalse(snippet.Contains("the client renders `#` as a literal"),
            "Blazor supports headings; the deny line must not appear");
        Assert.IsFalse(snippet.Contains("present tabular data as a bulleted"),
            "Blazor supports tables; the deny line must not appear");
    }

    [TestMethod]
    public void Build_Strikethrough_Alone_AddsAllowLine()
    {
        var snippet = ClientCapabilityPromptBuilder.Build(
            ClientCapabilities.Text | ClientCapabilities.MarkdownBasic |
            ClientCapabilities.MarkdownStrikethrough);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "strikethrough");
        StringAssert.Contains(snippet, "~~text~~");
        Assert.IsFalse(snippet.Contains("task-list"), "Task-list line must not leak in");
    }

    [TestMethod]
    public void Build_HtmlInline_IncludesSanitizerDenyList()
    {
        var snippet = ClientCapabilityPromptBuilder.Build(
            ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.HtmlInline);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "<script>");
        StringAssert.Contains(snippet, "<iframe>");
        StringAssert.Contains(snippet, "event handlers");
    }

    [TestMethod]
    public void Build_NoBasicMarkdown_DeniesAllFormatting()
    {
        // Edge case: caller sets only HtmlInline without MarkdownBasic. The prompt
        // should still gate plain-text-only at the markdown level.
        var snippet = ClientCapabilityPromptBuilder.Build(
            ClientCapabilities.Text | ClientCapabilities.HtmlInline);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "plain text only");
    }

    [TestMethod]
    public void Build_OutputEndsWithDefaultReminder()
    {
        var snippet = ClientCapabilityPromptBuilder.Build(ClientCapabilityPresets.Blazor);

        Assert.IsNotNull(snippet);
        StringAssert.Contains(snippet, "Plain markdown remains the default");
    }
}
