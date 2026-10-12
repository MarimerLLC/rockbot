extern alias fixture;

using System.Reflection;
using System.Text.Json;
using fixture::McpServer.ElicitationFixture.Tools;
using ModelContextProtocol.Server;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Elicitation;
using RockBot.UserProxy;
using static RockBot.Agent.Tests.McpBridge.McpHandbackEndToEndTests;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The live-test fixture server's tools (<c>src/McpServer.ElicitationFixture</c>) through the real
/// bridge, so the fixture is known to exercise what its README says before anyone deploys it.
/// </summary>
[TestClass]
public class ElicitationFixtureTests
{
    private const string Session = "session/test";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static IEnumerable<McpServerTool> FixtureTools() =>
        typeof(ElicitationFixtureTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, target: null));

    private static Task<BridgeHarness> StartAsync(IConversationMemory? conversation = null)
    {
        var capabilities = new SessionClientCapabilityStore();
        capabilities.Set("test", ClientCapabilityPresets.Blazor);
        return BridgeHarness.StartAsync(
            FixtureTools(),
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback },
            conversationMemory: conversation,
            clientCapabilities: capabilities);
    }

    private static List<McpQuestionForm> Forms(BridgeHarness harness) =>
        [.. harness.PublishedOn("user.response.test-agent")
            .Where(e => e.MessageType == typeof(AgentReply).FullName)
            .Select(e => e.GetPayload<AgentReply>()!)
            .Where(r => r.ContentType == McpQuestionForm.ContentType)
            .Select(r => JsonSerializer.Deserialize<McpQuestionForm>(r.StructuredData!, Json)!)];

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BookTable_ShowsItsMarkupLiterally_AndEchoesTheAnswer()
    {
        await using var harness = await StartAsync();
        var id = IdIn((await harness.InvokeAsync("book_table", """{"restaurant":"Nonna's"}""", sessionId: Session, canAnswer: true)).Content);

        var form = Forms(harness).Single();
        var note = form.Fields.Single(f => f.Name == "note");
        Assert.AreEqual("Note for the <b>kitchen</b>", note.Title, "markup arrives as text for the client to show literally");
        Assert.AreEqual(200, note.MaxLength);
        var size = form.Fields.Single(f => f.Name == "party_size");
        Assert.AreEqual(McpQuestionFieldKind.Integer, size.Kind);
        Assert.AreEqual(12, size.Maximum);
        Assert.AreEqual("date", form.Fields.Single(f => f.Name == "date").Format);

        var answered = await harness.AnswerAsync(id, """{"party_size":4,"date":"2026-10-20"}""", fromUser: true);

        StringAssert.StartsWith(answered.Result!.Content, "Booked a table for 4 at Nonna's on 2026-10-20.");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TwoStep_AsksTwice_AndTheSecondQuestionIsADecision()
    {
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(conversation);
        var first = IdIn((await harness.InvokeAsync("two_step", "{}", sessionId: Session, canAnswer: true)).Content);

        var next = await harness.AnswerAsync(first, """{"name":"Apollo"}""", fromUser: true);
        var second = IdIn(next.Result!.Content);
        Assert.IsTrue(Forms(harness).Single(f => f.QuestionId == second).Fields.Single().IsDecision);

        await conversation.AddTurnAsync("test", new ConversationTurn("user", "[Form] confirm: true", DateTimeOffset.UtcNow));
        var done = await harness.AnswerAsync(second, """{"confirm":true}""", fromUser: true);

        StringAssert.StartsWith(done.Result!.Content, "Created the project \"Apollo\".");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ChooseToppings_IsAMultiSelect_AndPickMeaningASelectOfFive()
    {
        await using var harness = await StartAsync();
        var toppings = IdIn((await harness.InvokeAsync("choose_toppings", "{}", sessionId: Session, canAnswer: true)).Content);
        IdIn((await harness.InvokeAsync("pick_meaning", """{"term":"Mercury"}""", sessionId: Session, canAnswer: true)).Content);

        var forms = Forms(harness);
        var many = forms.Single(f => f.QuestionId == toppings).Fields.Single(f => f.Name == "toppings");
        Assert.AreEqual(McpQuestionFieldKind.MultiSelect, many.Kind);
        Assert.AreEqual(3, many.MaxItems);
        var meaning = forms.Single(f => f.QuestionId != toppings).Fields.Single();
        Assert.AreEqual(5, meaning.Options!.Count);

        var ordered = await harness.AnswerAsync(toppings, """{"toppings":["olive","basil"]}""", fromUser: true);
        StringAssert.StartsWith(ordered.Result!.Content, "Ordered a pizza with olive, basil.");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Ping_AsksNothing()
    {
        await using var harness = await StartAsync();

        var result = await harness.InvokeAsync("ping", "{}", sessionId: Session, canAnswer: true);

        Assert.AreEqual("pong", result.Content);
        Assert.AreEqual(0, Forms(harness).Count);
    }
}
