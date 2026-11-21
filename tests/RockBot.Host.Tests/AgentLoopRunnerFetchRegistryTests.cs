using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// #847: a tool call that repeats a fetch the run already made — by the loop itself or by a wisp
/// step recorded in the registry the run was given — gets a note on its result saying where the
/// data already is. Covers both tool-calling paths.
/// </summary>
[TestClass]
public class AgentLoopRunnerFetchRegistryTests
{
    private static FunctionCallContent Search(string id, string query) =>
        new(id, "mail__search_emails", new Dictionary<string, object?> { ["query"] = query, ["count"] = 50 });

    [TestMethod]
    [DataRow(false, DisplayName = "native (function-invoking chat client)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task RepeatedFetch_GetsTheDuplicateNote(bool textBased)
    {
        var model = new ScriptedChatClient([Search("c1", "invoice"), Search("c2", "invoice")], "done");
        var runner = CreateRunner(model, textBased);

        await runner.RunAsync(
            [new(ChatRole.System, "test"), new(ChatRole.User, "Find invoices")],
            Options(), "session-1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        var results = ToolResults(model.LastMessages!);
        Assert.AreEqual(2, results.Count, string.Join(" | ", results));
        Assert.IsFalse(results[0].Contains("[System note: this exact call"), "The first fetch is not flagged.");
        StringAssert.Contains(results[1], "[System note: this exact call (mail__search_emails, same arguments)");
        StringAssert.Contains(results[1], "by an earlier direct call");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (function-invoking chat client)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task DifferentArguments_AreNotFlagged(bool textBased)
    {
        var model = new ScriptedChatClient([Search("c1", "invoice"), Search("c2", "receipt")], "done");
        var runner = CreateRunner(model, textBased);

        await runner.RunAsync(
            [new(ChatRole.System, "test"), new(ChatRole.User, "Find invoices")],
            Options(), "session-1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        Assert.IsTrue(ToolResults(model.LastMessages!).All(r => !r.Contains("[System note: this exact call")));
    }

    [TestMethod]
    public async Task FetchAWispAlreadyMade_NamesTheWispOutputKey()
    {
        // What a wisp Direct step records in its parent run's registry (an mcp_invoke_tool route).
        var registry = new RunFetchRegistry();
        registry.Observe("mcp_invoke_tool",
            """{"server_name":"mail","tool_name":"search_emails","arguments":{"count":50,"query":"invoice"}}""",
            succeeded: true, "by wisp wisp-1234 step search", key: "wisp/wisp-1234/search/output");

        var model = new ScriptedChatClient([Search("c1", "invoice")], "done");
        var runner = CreateRunner(model, textBased: false);

        await runner.RunAsync(
            [new(ChatRole.System, "test"), new(ChatRole.User, "Find invoices")],
            Options(), "worker-abc",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None,
            fetchRegistry: registry);

        var result = ToolResults(model.LastMessages!).Single();
        StringAssert.Contains(result, "by wisp wisp-1234 step search");
        StringAssert.Contains(result, "`wisp/wisp-1234/search/output`");
        StringAssert.Contains(result, "input_keys");
    }

    [TestMethod]
    public async Task ReReadAfterAWrite_IsNotFlagged()
    {
        var send = new FunctionCallContent("c2", "mail__send_email",
            new Dictionary<string, object?> { ["to"] = "a@example.com" });
        var model = new ScriptedChatClient([Search("c1", "invoice"), send, Search("c3", "invoice")], "done");
        var runner = CreateRunner(model, textBased: false);

        await runner.RunAsync(
            [new(ChatRole.System, "test"), new(ChatRole.User, "Find invoices")],
            Options(), "session-1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        Assert.IsTrue(ToolResults(model.LastMessages!).All(r => !r.Contains("[System note: this exact call")));
    }

    private static ChatOptions Options() => new()
    {
        Tools =
        [
            AIFunctionFactory.Create((string query, int count) => $"{count} results for {query}", "mail__search_emails"),
            AIFunctionFactory.Create((string to) => $"sent to {to}", "mail__send_email"),
        ]
    };

    /// <summary>The text of every tool result the model was shown, in order, on either path.</summary>
    private static List<string> ToolResults(IEnumerable<ChatMessage> messages)
    {
        var results = new List<string>();
        foreach (var m in messages)
        {
            foreach (var r in m.Contents.OfType<FunctionResultContent>())
                results.Add(r.Result?.ToString() ?? string.Empty);
            if (m.Role == ChatRole.User && m.Text.StartsWith("[Tool result for", StringComparison.Ordinal))
                results.Add(m.Text);
        }
        return results;
    }

    private static AgentLoopRunner CreateRunner(IChatClient model, bool textBased)
    {
        var profileOpts = Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-fetchreg-loop-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };
        var hostOptions = Microsoft.Extensions.Options.Options.Create(new AgentHostOptions());
        var workingMemory = new StubWorkingMemory();

        IChatClient client = textBased
            ? model
            : new RockBotFunctionInvokingChatClient(model, null, null, behavior,
                new LlmCostEstimator(
                    Microsoft.Extensions.Options.Options.Create(new LlmPricingOptions
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

    /// <summary>Returns one tool call per request, then the closing text; keeps the last request.</summary>
    private sealed class ScriptedChatClient(IReadOnlyList<FunctionCallContent> calls, string finalText) : IChatClient
    {
        private int _calls;

        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var n = Interlocked.Increment(ref _calls) - 1;
            var message = n < calls.Count
                ? new ChatMessage(ChatRole.Assistant, [calls[n]])
                : new ChatMessage(ChatRole.Assistant, finalText);
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

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
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
