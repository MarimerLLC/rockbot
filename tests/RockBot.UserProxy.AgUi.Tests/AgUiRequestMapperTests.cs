using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>How a <c>RunAgentInput</c> becomes a RockBot <see cref="UserMessage"/>, or is refused.</summary>
[TestClass]
public sealed class AgUiRequestMapperTests
{
    private static readonly AgUiOptions Options = new();

    private static RunAgentInput Input(string json) =>
        JsonSerializer.Deserialize(json, AGUIJsonSerializerContext.Default.RunAgentInput)!;

    private static AgUiRunRequest Map(string json, AgUiOptions? options = null) =>
        AgUiRequestMapper.Map(Input(json), options ?? Options, "agui");

    [TestMethod]
    public void LastUserMessage_IsTheOneSent()
    {
        var request = Map("""
            {"threadId":"agui-session","runId":"r1","messages":[
              {"id":"1","role":"user","content":"first"},
              {"id":"2","role":"assistant","content":"an answer"},
              {"id":"3","role":"user","content":"second"},
              {"id":"4","role":"assistant","content":"replayed by the client"}]}
            """);

        Assert.IsNotNull(request.Message);
        Assert.AreEqual("second", request.Message.Content);
        Assert.AreEqual("agui-session", request.Message.SessionId);
        Assert.AreEqual("agui-user", request.Message.UserId);
        Assert.AreEqual("agui", request.Message.ChannelName);
        Assert.AreEqual(ClientCapabilityPresets.AgUi, request.Message.ClientCapabilities);
    }

    [TestMethod]
    public void MultipartContent_SendsItsText()
    {
        var request = Map("""
            {"threadId":"agui-session","runId":"r1","messages":[
              {"id":"1","role":"user","content":[
                {"type":"text","text":"What is "},
                {"type":"text","text":"this?"}]}]}
            """);

        Assert.AreEqual("What is this?", request.Message?.Content);
    }

    [TestMethod]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[]}""", DisplayName = "no messages")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"assistant","content":"hi"}]}""", DisplayName = "no user message")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"  "}]}""", DisplayName = "blank user message")]
    [DataRow("""{"threadId":"agui-session","runId":"","messages":[{"id":"1","role":"user","content":"hi"}]}""", DisplayName = "no run id")]
    [DataRow("""{"threadId":"","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}]}""", DisplayName = "no thread id")]
    public void RequestWithNothingToSend_IsABadRequest(string json)
    {
        var request = Map(json);

        Assert.IsNull(request.Message);
        Assert.AreEqual(400, request.ErrorStatus);
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.Error));
    }

    [TestMethod]
    public void ThreadOutsideTheAllowList_IsForbidden()
    {
        var request = Map("""{"threadId":"someone-elses","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}]}""");

        Assert.IsNull(request.Message);
        Assert.AreEqual(403, request.ErrorStatus);
    }

    [TestMethod]
    public void Resume_IsNotSupportedYet()
    {
        var request = Map("""
            {"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}],
             "resume":[{"interruptId":"q1","status":"resolved","payload":{"answer":"yes"}}]}
            """);

        Assert.IsNull(request.Message);
        Assert.AreEqual(400, request.ErrorStatus);
        StringAssert.Contains(request.Error, "not supported");
    }

    [TestMethod]
    public void DefaultUserId_ComesFromOptions()
    {
        var request = Map(
            """{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}]}""",
            new AgUiOptions { DefaultUserId = "rocky" });

        Assert.AreEqual("rocky", request.Message?.UserId);
    }

    // ── Thread allow-list ────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("agui-session", true)]
    [DataRow("AGUI-SESSION", false, DisplayName = "the match is exact")]
    [DataRow("agui-session-2", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void WithNoListConfigured_OnlyTheDefaultThreadIsAllowed(string? threadId, bool allowed)
    {
        Assert.AreEqual(allowed, new AgUiOptions().IsThreadAllowed(threadId));
    }

    [TestMethod]
    public void AConfiguredList_ReplacesTheDefault()
    {
        var options = new AgUiOptions { AllowedThreadIds = ["phone", "laptop", " "] };

        Assert.IsTrue(options.IsThreadAllowed("phone"));
        Assert.IsTrue(options.IsThreadAllowed("laptop"));
        Assert.IsFalse(options.IsThreadAllowed("agui-session"));
        Assert.IsFalse(options.IsThreadAllowed(" "));
    }

    // ── Capabilities ─────────────────────────────────────────────────────────

    private static ClientCapabilities Capabilities(string? forwardedProps) =>
        AgUiRequestMapper.Capabilities(forwardedProps is null ? null : JsonDocument.Parse(forwardedProps).RootElement);

    [TestMethod]
    [DataRow(null, DisplayName = "no forwardedProps")]
    [DataRow("""{}""", DisplayName = "no rockbot object")]
    [DataRow("""{"rockbot":{}}""", DisplayName = "no capabilities")]
    [DataRow("""{"rockbot":{"capabilities":"MarkdownBasic"}}""", DisplayName = "capabilities is not an array")]
    [DataRow("""{"rockbot":"x"}""", DisplayName = "rockbot is not an object")]
    [DataRow("""[1,2]""", DisplayName = "forwardedProps is not an object")]
    public void WithoutDeclaredCapabilities_TheAgUiPresetApplies(string? forwardedProps)
    {
        Assert.AreEqual(ClientCapabilityPresets.AgUi, Capabilities(forwardedProps));
    }

    [TestMethod]
    public void DeclaredNames_BecomeFlags_CaseInsensitively()
    {
        var capabilities = Capabilities("""{"rockbot":{"capabilities":["markdownBasic","INTERACTIVEFORMS","HtmlInline"]}}""");

        Assert.AreEqual(
            ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.InteractiveForms | ClientCapabilities.HtmlInline,
            capabilities);
    }

    [TestMethod]
    public void UnknownNames_AndNonNames_AreIgnored()
    {
        // A number would set arbitrary bits, and a comma-separated list is not one name.
        var capabilities = Capabilities(
            """{"rockbot":{"capabilities":["MarkdownCode","Holograms","65536",65536,"HtmlInline,SvgInline",null,""]}}""");

        Assert.AreEqual(ClientCapabilities.Text | ClientCapabilities.MarkdownCode, capabilities);
    }

    [TestMethod]
    public void AnEmptyDeclaration_MeansTextOnly()
    {
        Assert.AreEqual(ClientCapabilities.Text, Capabilities("""{"rockbot":{"capabilities":[]}}"""));
    }

    [TestMethod]
    public void TheAgUiPreset_LeavesOutWhatAGenericClientMayNotHandle()
    {
        const ClientCapabilities preset = ClientCapabilityPresets.AgUi;

        foreach (var expected in new[]
        {
            ClientCapabilities.Text, ClientCapabilities.MarkdownBasic, ClientCapabilities.MarkdownHeadings,
            ClientCapabilities.MarkdownTables, ClientCapabilities.MarkdownCode, ClientCapabilities.MarkdownStrikethrough,
            ClientCapabilities.MarkdownTaskList, ClientCapabilities.LinkInline, ClientCapabilities.ImageAttachment,
        })
        {
            Assert.IsTrue(preset.HasFlag(expected), expected.ToString());
        }

        foreach (var absent in new[]
        {
            ClientCapabilities.HtmlInline, ClientCapabilities.SvgInline, ClientCapabilities.HtmlPreview,
            ClientCapabilities.InteractiveForms,
        })
        {
            Assert.IsFalse(preset.HasFlag(absent), absent.ToString());
        }
    }
}
