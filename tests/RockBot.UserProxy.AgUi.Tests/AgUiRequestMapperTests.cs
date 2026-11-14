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
    public void Resume_BecomesOneAnswerPerEntry_AndSendsNoMessage()
    {
        var request = Map("""
            {"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"replayed history"}],
             "resume":[{"interruptId":"q1","status":"resolved","payload":{"tz":"Europe/Brussels","count":2}},
                       {"interruptId":"q2","status":"cancelled","payload":{"ignored":true}}]}
            """);

        Assert.IsFalse(request.IsRefused, request.Error);
        Assert.IsNull(request.Message, "a resume run answers interrupts; the replayed messages are not new input");
        Assert.AreEqual(QuestionMode.Interrupt, request.QuestionMode);
        Assert.AreEqual(2, request.Resume!.Count);

        Assert.AreEqual("q1", request.Resume[0].QuestionId);
        Assert.IsFalse(request.Resume[0].Decline);
        using var answers = JsonDocument.Parse(request.Resume[0].Answers!);
        Assert.AreEqual("Europe/Brussels", answers.RootElement.GetProperty("tz").GetString());
        Assert.AreEqual(2, answers.RootElement.GetProperty("count").GetInt32());

        Assert.AreEqual(new AgUiResumeEntry("q2", Decline: true, Answers: null), request.Resume[1]);
    }

    [TestMethod]
    public void Resume_NeedsNoUserMessage()
    {
        var request = Map("""{"threadId":"agui-session","runId":"r1","messages":[],"resume":[{"interruptId":"q1","status":"cancelled"}]}""");

        Assert.IsFalse(request.IsRefused, request.Error);
        Assert.AreEqual(1, request.Resume!.Count);
    }

    [TestMethod]
    [DataRow("""[{"interruptId":"","status":"resolved","payload":{}}]""", "interruptId", DisplayName = "no interrupt id")]
    [DataRow("""[{"interruptId":"q1","status":"approved","payload":{}}]""", "status", DisplayName = "an unknown status")]
    [DataRow("""[{"interruptId":"q1","status":"resolved"}]""", "payload", DisplayName = "resolved without a payload")]
    [DataRow("""[{"interruptId":"q1","status":"resolved","payload":[1]}]""", "payload", DisplayName = "a payload that is not an object")]
    [DataRow("""[{"interruptId":"q1","status":"resolved","payload":{}},{"interruptId":"q1","status":"cancelled"}]""", "more than once", DisplayName = "one interrupt twice")]
    public void MalformedResume_IsRefused(string resume, string says)
    {
        var request = Map($$"""{"threadId":"agui-session","runId":"r1","messages":[],"resume":{{resume}}}""");

        Assert.AreEqual(400, request.ErrorStatus);
        Assert.IsNull(request.Resume);
        StringAssert.Contains(request.Error, says);
    }

    [TestMethod]
    public void AnEmptyResume_IsARunWithNewInput()
    {
        var request = Map("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}],"resume":[]}""");

        Assert.IsNull(request.Resume);
        Assert.AreEqual("hi", request.Message?.Content);
    }

    // ── Question mode ────────────────────────────────────────────────────────

    private static AgUiRunRequest MapWithProps(string rockbot) => Map(
        """{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}],"forwardedProps":{"rockbot":"""
        + rockbot + "}}");

    [TestMethod]
    public void WithoutForms_ThereIsNoQuestionMode()
    {
        var request = Map("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}]}""");

        Assert.AreEqual(QuestionMode.None, request.QuestionMode);
        Assert.IsFalse(request.Message!.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms));
    }

    [TestMethod]
    public void DeclaringInteractiveForms_WithoutAMode_IsInterruptMode()
    {
        var request = MapWithProps("""{"capabilities":["MarkdownBasic","InteractiveForms"]}""");

        Assert.AreEqual(QuestionMode.Interrupt, request.QuestionMode);
        Assert.IsTrue(request.Message!.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms));
    }

    [TestMethod]
    [DataRow("interrupt", nameof(QuestionMode.Interrupt))]
    [DataRow("custom", nameof(QuestionMode.Custom))]
    [DataRow("Custom", nameof(QuestionMode.Custom))]
    public void NamingAMode_SetsIt_AndDeclaresInteractiveForms(string name, string expected)
    {
        var request = MapWithProps($$"""{"questionMode":"{{name}}"}""");

        Assert.AreEqual(Enum.Parse<QuestionMode>(expected), request.QuestionMode);
        // Added to the default capabilities, which leave forms out.
        Assert.AreEqual(ClientCapabilityPresets.AgUi | ClientCapabilities.InteractiveForms, request.Message!.ClientCapabilities);
    }

    [TestMethod]
    public void NamingAMode_AddsInteractiveFormsToWhatTheClientListed()
    {
        var request = MapWithProps("""{"questionMode":"custom","capabilities":["MarkdownBasic"]}""");

        Assert.AreEqual(QuestionMode.Custom, request.QuestionMode);
        Assert.AreEqual(
            ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.InteractiveForms,
            request.Message!.ClientCapabilities);
    }

    [TestMethod]
    [DataRow("\"popup\"")]
    [DataRow("7")]
    [DataRow("[\"interrupt\"]")]
    public void AnUnknownMode_IsRefused(string value)
    {
        var request = MapWithProps($$"""{"questionMode":{{value}}}""");

        Assert.AreEqual(400, request.ErrorStatus);
        StringAssert.Contains(request.Error, "questionMode");
    }

    [TestMethod]
    public void ANullMode_IsNoMode()
    {
        var request = MapWithProps("""{"questionMode":null}""");

        Assert.IsFalse(request.IsRefused);
        Assert.AreEqual(QuestionMode.None, request.QuestionMode);
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

    // ── Attachments (phase 4) ────────────────────────────────────────────────

    private static AgUiRunRequest MapWithAttachments(string attachmentsJson, string content = "hi") => Map(
        "{\"threadId\":\"agui-session\",\"runId\":\"r1\",\"messages\":[{\"id\":\"1\",\"role\":\"user\",\"content\":\"" + content + "\"}],"
        + "\"forwardedProps\":{\"rockbot\":{\"attachments\":" + attachmentsJson + "}}}");

    [TestMethod]
    public void Attachments_AreReadFromForwardedProps_AsAnUploadReturnedThem()
    {
        var request = MapWithAttachments(
            """[{"mime":"image/png","path":"shot-1.png","fileName":"Screenshot.png","url":"/attachments/shot-1.png"},"""
            + """{"mime":"APPLICATION/PDF","path":"previews/ab/report.pdf"}]""");

        Assert.IsFalse(request.IsRefused, request.Error);
        var attachments = request.Message!.Attachments!;
        Assert.AreEqual(new AgentAttachment { Mime = "image/png", Path = "shot-1.png", FileName = "Screenshot.png" }, attachments[0]);
        // The type is the table's spelling, whatever case the client used.
        Assert.AreEqual(new AgentAttachment { Mime = "application/pdf", Path = "previews/ab/report.pdf" }, attachments[1]);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    public void NoAttachments_IsNull_NotAnEmptyList(string attachmentsJson)
    {
        var request = MapWithAttachments(attachmentsJson);

        Assert.IsFalse(request.IsRefused, request.Error);
        Assert.IsNull(request.Message!.Attachments);
    }

    [TestMethod]
    public void AMessageOfAttachmentsAlone_IsSentWithEmptyText()
    {
        var request = MapWithAttachments("""[{"mime":"image/png","path":"shot-1.png"}]""", content: "");

        Assert.IsFalse(request.IsRefused, request.Error);
        Assert.AreEqual("", request.Message!.Content);
        Assert.AreEqual(1, request.Message.Attachments!.Count);
    }

    [TestMethod]
    [DataRow("""[{"mime":"image/png","path":"../secret.png"}]""", DisplayName = "traversal")]
    [DataRow("""[{"mime":"image/png","path":"a/../../secret.png"}]""", DisplayName = "traversal inside")]
    [DataRow("""[{"mime":"image/png","path":"/rockbot/shared/exports/x.png"}]""", DisplayName = "absolute")]
    [DataRow("""[{"mime":"image/png","path":"C:/x.png"}]""", DisplayName = "drive")]
    [DataRow("""[{"mime":"image/png","path":"a\\x.png"}]""", DisplayName = "backslash")]
    [DataRow("""[{"mime":"image/png","path":"x.png\u0000.html"}]""", DisplayName = "control character")]
    [DataRow("""[{"mime":"text/html","path":"x.png"}]""", DisplayName = "type that isn't the extension's")]
    [DataRow("""[{"path":"x.png"}]""", DisplayName = "no type")]
    [DataRow("""[{"mime":"text/plain","path":"x.txt"}]""", DisplayName = "type that can't be attached")]
    [DataRow("""[{"mime":"image/png"}]""", DisplayName = "no path")]
    [DataRow("""[{"mime":"image/png","path":7}]""", DisplayName = "path that isn't a string")]
    [DataRow("""["x.png"]""", DisplayName = "entry that isn't an object")]
    [DataRow("""{"mime":"image/png","path":"x.png"}""", DisplayName = "not an array")]
    [DataRow("""[{"mime":"image/png","path":"x.png","fileName":"a\nb.png"}]""", DisplayName = "display name with a line break")]
    public void ABadAttachmentReference_Is400(string attachmentsJson)
    {
        var request = MapWithAttachments(attachmentsJson);

        Assert.AreEqual(400, request.ErrorStatus);
        StringAssert.Contains(request.Error, "forwardedProps.rockbot.attachments");
        Assert.IsNull(request.Message);
    }

    [TestMethod]
    public void MoreAttachmentsThanAMessageMayCarry_Is400()
    {
        var entry = """{"mime":"image/png","path":"x.png"}""";
        var atLimit = MapWithAttachments("[" + string.Join(",", Enumerable.Repeat(entry, AgUiRequestMapper.MaxAttachments)) + "]");
        var over = MapWithAttachments("[" + string.Join(",", Enumerable.Repeat(entry, AgUiRequestMapper.MaxAttachments + 1)) + "]");

        Assert.IsFalse(atLimit.IsRefused, atLimit.Error);
        Assert.AreEqual(400, over.ErrorStatus);
    }

    [TestMethod]
    public void ARunThatResumes_IgnoresAttachments()
    {
        var request = Map("""
            {"threadId":"agui-session","runId":"r1","messages":[],
             "resume":[{"interruptId":"q1","status":"cancelled"}],
             "forwardedProps":{"rockbot":{"attachments":"nonsense"}}}
            """);

        Assert.IsFalse(request.IsRefused, request.Error);
        Assert.IsNotNull(request.Resume);
    }
}