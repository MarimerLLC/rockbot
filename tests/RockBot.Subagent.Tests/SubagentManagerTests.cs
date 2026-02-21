using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentManagerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a SubagentManager with the given concurrency limit.
    /// The scope factory provides a SubagentRunner backed by a
    /// no-op (immediately-completing) LLM client so background tasks
    /// complete cleanly without needing real infrastructure.
    /// </summary>
    /// <summary>Registers all stubs needed by SubagentRunner into the service collection.</summary>
    private static void AddSubagentRunnerStubs(IServiceCollection services, ILlmClient llmClient)
    {
        services.AddSingleton(llmClient);
        services.AddSingleton<IWorkingMemory>(new NoopWorkingMemory());
        services.AddSingleton<IFeedbackStore>(new NoopFeedbackStore());
        services.AddSingleton<IToolRegistry>(new EmptyToolRegistry());
        services.AddSingleton<IMessagePublisher>(new NoopPublisher());
        services.AddSingleton<ILongTermMemory>(new NoopLongTermMemory());
        services.AddSingleton<ISkillStore>(new NoopSkillStore());
        services.AddSingleton(Options.Create(new AgentProfileOptions()));
        services.AddSingleton(new AgentIdentity("test-agent"));
        services.AddSingleton(ModelBehavior.Default);
        services.AddSingleton<MemoryTools>();
        services.AddTransient<AgentLoopRunner>();
        services.AddTransient<SubagentRunner>();
    }

    private static SubagentManager CreateManager(int maxConcurrent = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new NoopLlmClient());

        var provider = services.BuildServiceProvider();

        var opts = Options.Create(new SubagentOptions { MaxConcurrentSubagents = maxConcurrent });
        return new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            opts,
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("test-agent"),
            NullLogger<SubagentManager>.Instance);
    }

    /// <summary>
    /// Creates a SubagentManager whose SubagentRunner blocks until the
    /// <paramref name="blockUntil"/> TCS is signalled or the token is cancelled.
    /// Useful for testing in-flight task tracking and cancellation.
    /// </summary>
    private static (SubagentManager manager, TaskCompletionSource<bool> release) CreateBlockingManager()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddLogging();
        AddSubagentRunnerStubs(services, new BlockingLlmClient(tcs));

        var provider = services.BuildServiceProvider();

        var opts = Options.Create(new SubagentOptions { MaxConcurrentSubagents = 3 });
        var manager = new SubagentManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            opts,
            provider.GetRequiredService<IMessagePublisher>(),
            new AgentIdentity("test-agent"),
            NullLogger<SubagentManager>.Instance);

        return (manager, tcs);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SpawnAsync_ReturnsValidTaskId()
    {
        var manager = CreateManager();

        var result = await manager.SpawnAsync(
            "Test task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Should be a 12-char lowercase hex string, not an error
        Assert.IsFalse(result.StartsWith("Error:"),
            $"Expected task ID but got error: {result}");
        Assert.AreEqual(12, result.Length,
            $"Expected 12-char task ID but got '{result}'");
        Assert.IsTrue(result.All(c => "0123456789abcdef".Contains(c)),
            $"Task ID contains non-hex chars: {result}");
    }

    [TestMethod]
    public async Task SpawnAsync_WhenAtLimit_ReturnsErrorMessage()
    {
        // MaxConcurrentSubagents = 0 means any spawn is immediately rejected
        var manager = CreateManager(maxConcurrent: 0);

        var result = await manager.SpawnAsync(
            "Test task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        StringAssert.StartsWith(result, "Error:",
            $"Expected error message but got: {result}");
        StringAssert.Contains(result, "0");
    }

    [TestMethod]
    public async Task CancelAsync_UnknownId_ReturnsFalse()
    {
        var manager = CreateManager();

        var cancelled = await manager.CancelAsync("nonexistent-id");

        Assert.IsFalse(cancelled);
    }

    [TestMethod]
    public async Task CancelAsync_StopsRunningTask_ReturnsTrue()
    {
        var (manager, release) = CreateBlockingManager();

        // Spawn a task that blocks until we signal or cancel
        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session-1", ct: CancellationToken.None);

        Assert.IsFalse(taskId.StartsWith("Error:"), "Expected valid task ID");

        // Give the background task a moment to start (reach the LLM call)
        await Task.Delay(100);

        // Cancel should find and stop the task
        var cancelled = await manager.CancelAsync(taskId);

        Assert.IsTrue(cancelled, "CancelAsync should return true for an active task");

        // After cancel, the task should not be listed as active
        var active = manager.ListActive();
        Assert.IsFalse(active.Any(e => e.TaskId == taskId),
            "Cancelled task should not appear in ListActive");
    }

    [TestMethod]
    public async Task ListActive_ReturnsRunningTasks()
    {
        var (manager, release) = CreateBlockingManager();

        var taskId = await manager.SpawnAsync(
            "Blocking task", context: null, timeoutMinutes: 10,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Give the background task a moment to start
        await Task.Delay(100);

        var active = manager.ListActive();

        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(taskId, active[0].TaskId);
        Assert.AreEqual("Blocking task", active[0].Description);
        Assert.AreEqual("session-1", active[0].PrimarySessionId);

        // Clean up
        release.SetResult(true);
        await manager.CancelAsync(taskId);
    }

    [TestMethod]
    public async Task ListActive_AfterCompletion_ReturnsEmpty()
    {
        var manager = CreateManager(); // uses NoopLlmClient — completes immediately

        await manager.SpawnAsync(
            "Quick task", context: null, timeoutMinutes: null,
            primarySessionId: "session-1", ct: CancellationToken.None);

        // Wait briefly for the background task to complete
        await Task.Delay(200);

        var active = manager.ListActive();
        Assert.AreEqual(0, active.Count);
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    /// <summary>LLM client that immediately returns an empty response.</summary>
    private sealed class NoopLlmClient : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Return a minimal valid response with no tool calls
            var msg = new ChatMessage(ChatRole.Assistant, "Task complete.");
            return Task.FromResult(new ChatResponse([msg]));
        }
    }

    /// <summary>LLM client that blocks until the TCS is completed or the token is cancelled.</summary>
    private sealed class BlockingLlmClient(TaskCompletionSource<bool> tcs) : ILlmClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Block until either signal or cancellation
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task;
            cancellationToken.ThrowIfCancellationRequested();
            var msg = new ChatMessage(ChatRole.Assistant, "Done.");
            return new ChatResponse([msg]);
        }
    }

    private sealed class NoopWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string sessionId, string key, string value,
            TimeSpan? ttl = null, string? category = null,
            IReadOnlyList<string>? tags = null) => Task.CompletedTask;

        public Task<string?> GetAsync(string sessionId, string key) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string sessionId, string key) => Task.CompletedTask;

        public Task ClearAsync(string sessionId) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class NoopFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);

        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolRegistration> GetTools() => [];
        public IToolExecutor? GetExecutor(string toolName) => null;
        public void Register(ToolRegistration registration, IToolExecutor executor) { }
        public bool Unregister(string toolName) => false;
    }

    private sealed class NoopLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class NoopPublisher : IMessagePublisher
    {
        public Task PublishAsync(string topic, MessageEnvelope envelope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
