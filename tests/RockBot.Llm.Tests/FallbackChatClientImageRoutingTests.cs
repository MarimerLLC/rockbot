using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Llm.Tests;

/// <summary>
/// #818: a fallback chain must not hand an image to a model that cannot read it. A blind model
/// answers with a 400, which classifies as Unknown and ends the chain, so one blind fallback
/// would also cut off every later model that could have answered.
/// </summary>
[TestClass]
public class FallbackChatClientImageRoutingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private static List<ChatMessage> TextRequest() =>
        [new(ChatRole.User, "hello")];

    private static List<ChatMessage> ImageRequest(string mediaType = "image/png") =>
        [new(ChatRole.User, [new TextContent("what is this?"), new DataContent(Png, mediaType)])];

    private static FallbackChatClient Build(
        IReadOnlyList<(string, IChatClient)> entries, IReadOnlyList<bool>? images) =>
        new(entries, NullLogger.Instance, retryDelay: TimeSpan.Zero, maxRetries: 0,
            supportsImageInput: images);

    [TestMethod]
    public async Task AnImageRequestSkipsABlindPrimaryAndGoesToTheFirstModelThatCanSee()
    {
        var blind = new Stub("blind");
        var sees = new Stub("sees");
        var client = Build([("blind", blind), ("sees", sees)], [false, true]);

        var response = await client.GetResponseAsync(ImageRequest());

        Assert.AreEqual("sees", response.Text);
        Assert.AreEqual(0, blind.Calls);
    }

    [TestMethod]
    public async Task ATextRequestStillUsesTheBlindPrimary()
    {
        var blind = new Stub("blind");
        var sees = new Stub("sees");
        var client = Build([("blind", blind), ("sees", sees)], [false, true]);

        var response = await client.GetResponseAsync(TextRequest());

        Assert.AreEqual("blind", response.Text);
        Assert.AreEqual(0, sees.Calls);
    }

    [TestMethod]
    public async Task FailingOverFromASeeingModelSkipsBlindOnesInBetween()
    {
        // The live failure: primary sees, gets a 503, and the next entry is blind.
        var primary = new Stub("primary", new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));
        var blind = new Stub("blind");
        var last = new Stub("last");
        var client = Build([("primary", primary), ("blind", blind), ("last", last)], [true, false, true]);

        var response = await client.GetResponseAsync(ImageRequest());

        Assert.AreEqual("last", response.Text);
        Assert.AreEqual(0, blind.Calls);
    }

    [TestMethod]
    public async Task ARateLimitedSeeingModelIsNotSkippedWhenOnlyBlindModelsRemain()
    {
        // The 429 look-ahead must ask "is there another model that can take THIS request?".
        // If it counted the blind entry, the image request would go nowhere.
        var sees = new Stub("sees",
            new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests), failures: 1);
        var blind = new Stub("blind");
        var client = new FallbackChatClient([("sees", sees), ("blind", blind)], NullLogger.Instance,
            retryDelay: TimeSpan.Zero, maxRetries: 1,
            defaultRateLimitPause: TimeSpan.Zero, supportsImageInput: [true, false]);

        var response = await client.GetResponseAsync(ImageRequest());

        Assert.AreEqual("sees", response.Text, "with nowhere else to go, the throttled model is waited out and retried");
        Assert.AreEqual(2, sees.Calls);
        Assert.AreEqual(0, blind.Calls);
    }

    [TestMethod]
    public async Task AChainWithNoSeeingModelFailsClearlyWithoutCallingAnyModel()
    {
        var a = new Stub("a");
        var b = new Stub("b");
        var client = Build([("a", a), ("b", b)], [false, false]);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => client.GetResponseAsync(ImageRequest()));

        StringAssert.Contains(ex.Message, "no model in the chain declares SupportsImageInput");
        Assert.AreEqual(0, a.Calls + b.Calls);
    }

    [TestMethod]
    public async Task WhenEverySeeingModelFailsTheErrorSaysSoRatherThanAllDegraded()
    {
        var sees = new Stub("sees", new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));
        var blind = new Stub("blind");
        var client = Build([("sees", sees), ("blind", blind)], [true, false]);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => client.GetResponseAsync(ImageRequest()));

        StringAssert.Contains(ex.Message, "can read images");
        Assert.AreEqual(0, blind.Calls);
    }

    [TestMethod]
    public async Task WithoutDeclaredSupportEveryModelIsTriedAsBefore()
    {
        var a = new Stub("a");
        var client = Build([("a", a), ("b", new Stub("b"))], images: null);

        var response = await client.GetResponseAsync(ImageRequest());

        Assert.AreEqual("a", response.Text);
    }

    [TestMethod]
    public async Task APdfNeedsAModelThatCanSee()
    {
        var blind = new Stub("blind");
        var client = Build([("blind", blind), ("sees", new Stub("sees"))], [false, true]);

        var response = await client.GetResponseAsync(ImageRequest("application/pdf"));

        Assert.AreEqual("sees", response.Text);
    }

    [TestMethod]
    public void ImagesInToolResultsAndUrisCount()
    {
        var toolResult = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1",
                new List<AIContent> { new TextContent("screenshot"), new DataContent(Png, "image/png") })])
        };
        var uri = new List<ChatMessage>
        {
            new(ChatRole.User, [new UriContent(new Uri("https://example.com/a.jpg"), "image/jpeg")])
        };
        var audio = new List<ChatMessage>
        {
            new(ChatRole.User, [new DataContent(Png, "audio/wav")])
        };

        Assert.IsTrue(FallbackChatClient.RequiresImageInput(toolResult));
        Assert.IsTrue(FallbackChatClient.RequiresImageInput(uri));
        Assert.IsFalse(FallbackChatClient.RequiresImageInput(audio), "SupportsImageInput says nothing about audio");
        Assert.IsFalse(FallbackChatClient.RequiresImageInput(TextRequest()));
    }

    [TestMethod]
    public async Task StreamingAnImageRequestStartsOnAModelThatCanSee()
    {
        var blind = new Stub("blind");
        var sees = new Stub("sees");
        var client = Build([("blind", blind), ("sees", sees)], [false, true]);

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync(ImageRequest()))
            text += update.Text;

        Assert.AreEqual("sees", text);
        Assert.AreEqual(0, blind.Calls);
    }

    [TestMethod]
    public void AMismatchedFlagCountIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Build([("a", new Stub("a")), ("b", new Stub("b"))], [true]));
    }

    /// <summary>
    /// Answers with its own name, or throws the given exception for the first
    /// <paramref name="failures"/> calls (every call by default).
    /// </summary>
    private sealed class Stub(string name, Exception? ex = null, int failures = int.MaxValue) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ex is not null && Calls <= failures
                ? Task.FromException<ChatResponse>(ex)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, name)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, name);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
