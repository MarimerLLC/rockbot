using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class ClientCapabilitiesTests
{
    [TestMethod]
    public void UserMessage_DefaultsClientCapabilities_ToNone()
    {
        var message = new UserMessage { Content = "x", SessionId = "s", UserId = "u" };

        Assert.AreEqual(ClientCapabilities.None, message.ClientCapabilities);
    }

    [TestMethod]
    public void UserMessage_RoundTrips_ClientCapabilities()
    {
        var original = new UserMessage
        {
            Content = "Hello",
            SessionId = "s1",
            UserId = "u1",
            ClientCapabilities = ClientCapabilityPresets.Blazor
        };

        var envelope = original.ToEnvelope<UserMessage>(source: "proxy");
        var deserialized = envelope.GetPayload<UserMessage>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(ClientCapabilityPresets.Blazor, deserialized.ClientCapabilities);
    }

    [TestMethod]
    public void ClientCapabilities_FlagsCombine_AsBitwiseOr()
    {
        var combined = ClientCapabilities.MarkdownBasic | ClientCapabilities.HtmlInline;

        Assert.IsTrue(combined.HasFlag(ClientCapabilities.MarkdownBasic));
        Assert.IsTrue(combined.HasFlag(ClientCapabilities.HtmlInline));
        Assert.IsFalse(combined.HasFlag(ClientCapabilities.SvgInline));
    }

    [TestMethod]
    public void BlazorPreset_IncludesRichRendering()
    {
        var preset = ClientCapabilityPresets.Blazor;

        Assert.IsTrue(preset.HasFlag(ClientCapabilities.MarkdownTables));
        Assert.IsTrue(preset.HasFlag(ClientCapabilities.MarkdownStrikethrough));
        Assert.IsTrue(preset.HasFlag(ClientCapabilities.MarkdownTaskList));
        Assert.IsTrue(preset.HasFlag(ClientCapabilities.HtmlInline));
        Assert.IsTrue(preset.HasFlag(ClientCapabilities.SvgInline));
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.ImageAttachment),
            "Blazor preset should not declare ImageAttachment until AgentReply.Attachments lands");
    }

    [TestMethod]
    public void CliPreset_IsMarkdownOnly()
    {
        var preset = ClientCapabilityPresets.Cli;

        Assert.IsTrue(preset.HasFlag(ClientCapabilities.MarkdownBasic));
        Assert.IsTrue(preset.HasFlag(ClientCapabilities.MarkdownCode));
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.HtmlInline),
            "CLI preset must not advertise HTML — a terminal cannot render it");
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.SvgInline));
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.MarkdownTables));
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.MarkdownStrikethrough),
            "Terminals don't render `~~text~~` — leave it as literal characters");
        Assert.IsFalse(preset.HasFlag(ClientCapabilities.MarkdownTaskList));
    }

    [TestMethod]
    public void ChatPlatformPresets_AllAdvertiseStrikethrough()
    {
        // Strikethrough is the one GFM-beyond-CommonMark feature universally supported
        // across Discord / Slack / WhatsApp / Teams. The presets should reflect that.
        Assert.IsTrue(ClientCapabilityPresets.Discord.HasFlag(ClientCapabilities.MarkdownStrikethrough));
        Assert.IsTrue(ClientCapabilityPresets.Slack.HasFlag(ClientCapabilities.MarkdownStrikethrough));
        Assert.IsTrue(ClientCapabilityPresets.WhatsApp.HasFlag(ClientCapabilities.MarkdownStrikethrough));
        Assert.IsTrue(ClientCapabilityPresets.Teams.HasFlag(ClientCapabilities.MarkdownStrikethrough));
    }

    [TestMethod]
    public void TaskList_IsTeamsAndBlazorOnly_AmongDeclaredPresets()
    {
        // Task-list checkboxes are rendered natively only by Markdig advanced (Blazor)
        // and Teams. Other chat platforms show them as literal `- [ ]`.
        Assert.IsTrue(ClientCapabilityPresets.Blazor.HasFlag(ClientCapabilities.MarkdownTaskList));
        Assert.IsTrue(ClientCapabilityPresets.Teams.HasFlag(ClientCapabilities.MarkdownTaskList));
        Assert.IsFalse(ClientCapabilityPresets.Discord.HasFlag(ClientCapabilities.MarkdownTaskList));
        Assert.IsFalse(ClientCapabilityPresets.Slack.HasFlag(ClientCapabilities.MarkdownTaskList));
        Assert.IsFalse(ClientCapabilityPresets.WhatsApp.HasFlag(ClientCapabilities.MarkdownTaskList));
    }

    [TestMethod]
    public void Enum_SerializesAsInteger_ForCompactWireFormat()
    {
        // Default System.Text.Json options serialize flags enums numerically — this is
        // load-bearing for the design's "minimal wire footprint" guarantee. If a global
        // JsonStringEnumConverter were ever added, the integer would inflate to a string
        // like "MarkdownBasic, HtmlInline, SvgInline" and balloon the wire size.
        var message = new UserMessage
        {
            Content = "x",
            SessionId = "s",
            UserId = "u",
            ClientCapabilities = ClientCapabilityPresets.Blazor
        };

        var envelope = message.ToEnvelope<UserMessage>(source: "proxy");
        var jsonBody = System.Text.Encoding.UTF8.GetString(envelope.Body.Span);

        // Expect a number like "clientCapabilities":131 (or whatever bits add up to).
        // The exact value isn't asserted — what matters is the absence of textual flag names.
        Assert.IsTrue(jsonBody.Contains("\"clientCapabilities\":"),
            $"Body should contain the clientCapabilities key. Body was: {jsonBody}");
        Assert.IsFalse(jsonBody.Contains("MarkdownBasic"),
            $"Body should not contain the textual flag names. Body was: {jsonBody}");
    }
}
