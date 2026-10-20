using Microsoft.Extensions.AI;
using RockBot.Llm;

namespace RockBot.Host.Tests;

[TestClass]
public class ModelBehaviorPromptChatClientTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage>? Captured { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Captured = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Captured = messages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ModelBehaviorPromptChatClient Create(
        CapturingChatClient inner, string? prompt, string modelId = "gpt-5.6-terra") =>
        new(inner, modelId, _ => new ModelBehavior { AdditionalSystemPrompt = prompt });

    /// <summary>System prompt, rules, optional model-behavior message, then the user turn — as the context builder lays it out.</summary>
    private static List<ChatMessage> BuilderMessages(string? startupPrompt)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "persona"),
            new ChatMessage(ChatRole.System, "rules").Tag(ContextCategory.Rules),
        };
        if (startupPrompt is not null)
            messages.Add(new ChatMessage(ChatRole.System, startupPrompt).Tag(ContextCategory.ModelBehavior));
        messages.Add(new ChatMessage(ChatRole.System, "memory recall").Tag(ContextCategory.LongTermMemory));
        messages.Add(new ChatMessage(ChatRole.User, "hello").Tag(ContextCategory.HistoryUser));
        return messages;
    }

    private static string[] Texts(IEnumerable<ChatMessage> messages) => messages.Select(m => m.Text).ToArray();

    [TestMethod]
    public async Task InjectsOwnPrompt_AfterRules_WhenBuilderInjectedNone()
    {
        var inner = new CapturingChatClient();
        var sut = Create(inner, "gpt voice");

        await sut.GetResponseAsync(BuilderMessages(startupPrompt: null));

        CollectionAssert.AreEqual(
            new[] { "persona", "rules", "gpt voice", "memory recall", "hello" }, Texts(inner.Captured!));
        Assert.AreEqual(ContextCategory.ModelBehavior, ContextTags.Get(inner.Captured![2]));
    }

    [TestMethod]
    public async Task ReplacesStartupModelsPrompt_InPlace()
    {
        var inner = new CapturingChatClient();
        var sut = Create(inner, "gpt voice");

        await sut.GetResponseAsync(BuilderMessages(startupPrompt: "claude voice"));

        CollectionAssert.AreEqual(
            new[] { "persona", "rules", "gpt voice", "memory recall", "hello" }, Texts(inner.Captured!));
    }

    [TestMethod]
    public async Task RemovesStartupModelsPrompt_WhenServingModelHasNone()
    {
        var inner = new CapturingChatClient();
        var sut = Create(inner, prompt: null, modelId: "claude-sonnet-5-5");

        await sut.GetResponseAsync(BuilderMessages(startupPrompt: "gpt voice"));

        CollectionAssert.AreEqual(
            new[] { "persona", "rules", "memory recall", "hello" }, Texts(inner.Captured!));
    }

    [TestMethod]
    public void PassesThroughUntouched_WhenBuilderAlreadyInjectedThisPrompt()
    {
        var sut = Create(new CapturingChatClient(), "gpt voice");
        var messages = BuilderMessages(startupPrompt: "gpt voice");

        Assert.AreSame(messages, sut.Apply(messages));
    }

    [TestMethod]
    public void PassesThroughUntouched_WhenNeitherSideHasAPrompt()
    {
        var sut = Create(new CapturingChatClient(), prompt: null);
        var messages = BuilderMessages(startupPrompt: null);

        Assert.AreSame(messages, sut.Apply(messages));
    }

    [TestMethod]
    public void LeavesRequestAlone_UntilBehaviorProviderIsReady_ThenResolves()
    {
        ModelBehavior? behavior = null;
        var resolveCount = 0;
        var sut = new ModelBehaviorPromptChatClient(new CapturingChatClient(), "gpt-5.6-terra", _ =>
        {
            resolveCount++;
            return behavior;
        });
        var messages = BuilderMessages(startupPrompt: null);

        Assert.AreSame(messages, sut.Apply(messages));

        behavior = new ModelBehavior { AdditionalSystemPrompt = "gpt voice" };
        Assert.IsTrue(Texts(sut.Apply(messages)).Contains("gpt voice"));
        sut.Apply(messages);

        Assert.AreEqual(2, resolveCount, "resolved once the provider answered, then cached");
    }

    [TestMethod]
    public async Task Streaming_GetsTheSamePrompt()
    {
        var inner = new CapturingChatClient();
        var sut = Create(inner, "gpt voice");

        await foreach (var _ in sut.GetStreamingResponseAsync(BuilderMessages(startupPrompt: "claude voice"))) { }

        CollectionAssert.AreEqual(
            new[] { "persona", "rules", "gpt voice", "memory recall", "hello" }, Texts(inner.Captured!));
    }

    [TestMethod]
    public void InsertsAfterSystemPrompt_WhenNoRulesOrCapabilities()
    {
        var sut = Create(new CapturingChatClient(), "gpt voice");
        var messages = new List<ChatMessage> { new(ChatRole.System, "persona"), new(ChatRole.User, "hi") };

        CollectionAssert.AreEqual(new[] { "persona", "gpt voice", "hi" }, Texts(sut.Apply(messages)));
    }
}
