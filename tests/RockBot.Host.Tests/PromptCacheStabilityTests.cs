using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Prompt caching only pays when each request in a tool loop extends the previous one (#710).
/// These drive a long, trim-heavy loop through <see cref="AgentLoopRunner.RunAsync"/> on both
/// tool-calling paths and check what the provider would have received.
/// </summary>
[TestClass]
public class PromptCacheStabilityTests
{
    private const int ToolCalls = 16;

    [TestMethod]
    [DataRow(false, DisplayName = "native (function-invoking chat client)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task LongToolLoop_RequestsExtendThePreviousOne_ExceptOnRareTrims(bool textBased)
    {
        var (requests, stashKeys) = await RunLoopAsync(textBased);

        Assert.AreEqual(ToolCalls + 1, requests.Count, "one request per tool call, plus the closing one");
        var rewrites = 0;
        for (var i = 1; i < requests.Count; i++)
        {
            if (!IsPrefix(requests[i - 1], requests[i]))
                rewrites++;
        }

        // Without the step down, the trim fired on nearly every request once the budget was
        // crossed (~10 of these); and with the old registry placement every new stash entry
        // rewrote the conversation ahead of the whole loop.
        Assert.IsTrue(stashKeys.Count > 0, "the fixture must exercise the stash");
        Assert.IsTrue(rewrites is > 0 and <= 4,
            $"{rewrites} of {requests.Count - 1} requests rewrote earlier history; the trim should step down rarely.");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (function-invoking chat client)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task LongToolLoop_NotesNeverSplitAToolTurn_AndListEveryStashKey(bool textBased)
    {
        var (requests, stashKeys) = await RunLoopAsync(textBased);

        foreach (var request in requests)
            AssertToolTurnsIntact(request);

        var noted = requests[^1]
            .Where(m => m.StartsWith("system|[stash-registry]", StringComparison.Ordinal))
            .ToList();
        foreach (var key in stashKeys)
        {
            Assert.IsTrue(noted.Any(n => n.Contains($"key={key}", StringComparison.Ordinal)),
                $"stash key {key} must reach the model in a [stash-registry] note");
        }
    }

    private static async Task<(List<List<string>> Requests, List<string> StashKeys)> RunLoopAsync(bool textBased)
    {
        var model = new RecordingToolModel(ToolCalls);
        var workingMemory = new RecordingWorkingMemory();
        var hostOptions = Options.Create(new AgentHostOptions
        {
            // ~21,600-char budget; results are 2,500 chars, and every fourth is over the
            // 8,000-char per-result cap so it is stashed when it arrives.
            ToolResultStashWatermarkTokens = 6_000,
        });
        var runner = CreateRunner(model, textBased, hostOptions, workingMemory);
        var calls = 0;
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((int n) =>
            {
                var size = Interlocked.Increment(ref calls) % 4 == 0 ? 9_000 : 2_500;
                return $"result {n}: " + new string((char)('a' + n % 26), size);
            }, "fetch")]
        };

        await runner.RunAsync(
            [new(ChatRole.System, "You are a test agent."), new(ChatRole.User, "Fetch everything")],
            options, "session/cache-test",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        return (model.Requests, workingMemory.Keys);
    }

    private static bool IsPrefix(List<string> earlier, List<string> later) =>
        earlier.Count <= later.Count && earlier.SequenceEqual(later.Take(earlier.Count));

    /// <summary>Every assistant tool-call turn is followed directly by its tool results.</summary>
    private static void AssertToolTurnsIntact(List<string> request)
    {
        for (var i = 0; i < request.Count; i++)
        {
            if (!request[i].StartsWith("assistant|call:", StringComparison.Ordinal))
                continue;
            var callId = request[i].Split(':')[1];
            if (i + 1 < request.Count)
            {
                StringAssert.StartsWith(request[i + 1], $"tool|result:{callId}:",
                    $"message {i + 1} must be the result for {callId}, not a note");
            }
        }
    }

    private static string Signature(ChatMessage message) =>
        message.Role.Value + "|" + string.Join("+", message.Contents.Select(c => c switch
        {
            FunctionCallContent call => $"call:{call.CallId}:{call.Name}",
            FunctionResultContent result => $"result:{result.CallId}:{result.Result}",
            TextContent text => text.Text,
            _ => c.GetType().Name,
        }));

    private static AgentLoopRunner CreateRunner(
        IChatClient model, bool textBased, IOptions<AgentHostOptions> hostOptions, IWorkingMemory workingMemory)
    {
        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-cache-loop-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };

        IChatClient client = textBased
            ? model
            : new RockBotFunctionInvokingChatClient(model, null, null, behavior,
                new LlmCostEstimator(
                    Options.Create(new LlmPricingOptions
                    {
                        ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json")
                    }),
                    NullLogger<LlmCostEstimator>.Instance),
                workingMemory, hostOptions, NullLogger.Instance);

        return new AgentLoopRunner(
            new ChatClientLlm(client),
            workingMemory,
            behavior,
            new StubFeedbackStore(),
            clock,
            hostOptions,
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);
    }

    /// <summary>
    /// Calls <c>fetch</c> once per request until <paramref name="toolCalls"/> calls are made, then
    /// answers. Records every request as the provider would receive it.
    /// </summary>
    private sealed class RecordingToolModel(int toolCalls) : IChatClient
    {
        public List<List<string>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.Select(Signature).ToList());
            var n = Requests.Count;
            var message = n <= toolCalls
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{n}", "fetch", new Dictionary<string, object?> { ["n"] = n })])
                : new ChatMessage(ChatRole.Assistant, "Fetched everything.");
            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ChatClientLlm(IChatClient client) : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);
    }

    private sealed class RecordingWorkingMemory : IWorkingMemory
    {
        public List<string> Keys { get; } = [];

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            if (key.StartsWith("stash/", StringComparison.Ordinal))
                lock (Keys) Keys.Add(key);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class StubFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }
}
