using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class LlmObservationExtractorTests
{
    private static ObservationTarget MakeTarget() => new()
    {
        Name = "test",
        Filter = new PassThrough(),
        ExtractionPrompt = "Extract observations.",
        EvaluationPrompt = "Evaluate.",
        StateFilePath = "/tmp/x.json",
        OutputMarkdownPath = "/tmp/x.md",
    };

    private static IReadOnlyList<TranscriptTurn> MakeConversation(string convId, params (string Id, string Content)[] turns) =>
        turns.Select(t => new TranscriptTurn(convId, t.Id, "user", "user", t.Content, DateTimeOffset.UtcNow)).ToArray();

    [TestMethod]
    public async Task ExtractAsync_WellFormedJson_ReturnsProposals()
    {
        var stub = new StubLlmClient();
        stub.AddResponse("conv1",
            """
            {
              "observations": [
                { "text": "Claim A", "conversationId": "conv1", "turnId": "t1", "quote": "verbatim quote" }
              ]
            }
            """);

        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "verbatim quote here"));

        var result = await extractor.ExtractAsync(MakeTarget(), turns, CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Claim A", result[0].Text);
        Assert.AreEqual("conv1", result[0].ConversationId);
        Assert.AreEqual("t1", result[0].TurnId);
        Assert.AreEqual("verbatim quote", result[0].Quote);
    }

    [TestMethod]
    public async Task ExtractAsync_EmptyTurnsList_ReturnsEmptyWithoutCallingLlm()
    {
        var stub = new StubLlmClient();
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);

        var result = await extractor.ExtractAsync(MakeTarget(), [], CancellationToken.None);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, stub.CallCount, "Empty input should short-circuit before calling LLM");
    }

    [TestMethod]
    public async Task ExtractAsync_LlmThrows_ReturnsEmptyAndDoesNotPropagate()
    {
        var stub = new StubLlmClient().ThrowFor("conv1");
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "anything"));

        var result = await extractor.ExtractAsync(MakeTarget(), turns, CancellationToken.None);

        Assert.AreEqual(0, result.Count, "Routine LLM failure should be swallowed; surrounding pipeline applies skip-and-continue");
    }

    [TestMethod]
    public async Task ExtractAsync_Cancelled_Throws()
    {
        var stub = new StubLlmClient();
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "anything"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await extractor.ExtractAsync(MakeTarget(), turns, cts.Token));
    }

    [TestMethod]
    public async Task ExtractAsync_MalformedJson_ReturnsEmpty()
    {
        var stub = new StubLlmClient().AddResponse("conv1", "{ this is not json");
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "anything"));

        var result = await extractor.ExtractAsync(MakeTarget(), turns, CancellationToken.None);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ExtractAsync_NarratedJsonResponse_ExtractsBalancedObject()
    {
        // Some models prepend or append commentary even with JSON mode requested.
        var stub = new StubLlmClient().AddResponse("conv1",
            """
            Sure, here is the result:
            {
              "observations": [
                { "text": "Claim", "conversationId": "conv1", "turnId": "t1", "quote": "the quote" }
              ]
            }
            Hope that helps!
            """);
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "anything"));

        var result = await extractor.ExtractAsync(MakeTarget(), turns, CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Claim", result[0].Text);
    }

    [TestMethod]
    public async Task ExtractAsync_DropsObservationsWithMissingFields()
    {
        var stub = new StubLlmClient().AddResponse("conv1",
            """
            {
              "observations": [
                { "text": "ok", "conversationId": "conv1", "turnId": "t1", "quote": "quote here" },
                { "text": "", "conversationId": "conv1", "turnId": "t1", "quote": "x" },
                { "conversationId": "conv1", "turnId": "t1", "quote": "x" },
                { "text": "no quote", "conversationId": "conv1", "turnId": "t1" }
              ]
            }
            """);
        var extractor = new LlmObservationExtractor(stub, NullLogger<LlmObservationExtractor>.Instance);
        var turns = MakeConversation("conv1", ("t1", "anything"));

        var result = await extractor.ExtractAsync(MakeTarget(), turns, CancellationToken.None);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ok", result[0].Text);
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }
}
