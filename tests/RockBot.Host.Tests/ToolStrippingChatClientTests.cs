using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

[TestClass]
public class ToolStrippingChatClientTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? CapturedOptions { get; private set; }
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            CapturedOptions = options;
            CallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatOptions OptionsWithTools() => new()
    {
        Tools = [AIFunctionFactory.Create(() => "result", "SaveMemory")],
        ToolMode = ChatToolMode.Auto,
        Temperature = 0.5f,
    };

    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "hi")];

    [TestMethod]
    public async Task GetResponseAsync_RemovesToolsFromForwardedOptions()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);

        await sut.GetResponseAsync(Messages, OptionsWithTools());

        Assert.IsNull(inner.CapturedOptions!.Tools);
        Assert.IsNull(inner.CapturedOptions.ToolMode);
    }

    [TestMethod]
    public async Task GetResponseAsync_PreservesOtherOptions()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);

        await sut.GetResponseAsync(Messages, OptionsWithTools());

        Assert.AreEqual(0.5f, inner.CapturedOptions!.Temperature);
    }

    [TestMethod]
    public async Task GetResponseAsync_DoesNotMutateCallersOptions()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);
        var options = OptionsWithTools();

        await sut.GetResponseAsync(Messages, options);

        // AgentLoopRunner reuses this instance to resolve and dispatch parsed tool calls,
        // so the caller's tool list must survive the request untouched.
        Assert.IsNotNull(options.Tools);
        Assert.AreEqual(1, options.Tools!.Count);
        Assert.AreEqual(ChatToolMode.Auto, options.ToolMode);
    }

    [TestMethod]
    public async Task GetResponseAsync_PassesThroughWhenNoTools()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);
        var options = new ChatOptions { Temperature = 0.2f };

        await sut.GetResponseAsync(Messages, options);

        Assert.AreSame(options, inner.CapturedOptions);
    }

    [TestMethod]
    public async Task GetResponseAsync_PassesThroughNullOptions()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);

        await sut.GetResponseAsync(Messages, null);

        Assert.IsNull(inner.CapturedOptions);
        Assert.AreEqual(1, inner.CallCount);
    }

    [TestMethod]
    public async Task GetStreamingResponseAsync_RemovesToolsFromForwardedOptions()
    {
        var inner = new CapturingChatClient();
        var sut = new ToolStrippingChatClient(inner);
        var options = OptionsWithTools();

        await foreach (var _ in sut.GetStreamingResponseAsync(Messages, options)) { }

        Assert.IsNull(inner.CapturedOptions!.Tools);
        Assert.IsNotNull(options.Tools);
    }
}
