using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="RockBotFunctionInvokingChatClient"/> is a per-tier singleton that concurrent runs
/// share. Its loop tracking (repeated identical calls, consecutive timeouts) must belong to each
/// <c>GetResponseAsync</c> call, not the instance: another run starting mid-loop must not reset it.
/// </summary>
[TestClass]
public class FunctionInvokingCallStateTests
{
    private const string Nudge = "[System note: You have called probe";

    [TestMethod]
    public async Task AnotherCallStartingMidLoop_DoesNotResetStuckLoopDetection()
    {
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        var probe = AIFunctionFactory.Create(async (int x) =>
        {
            // The second identical call waits until another run has entered the same client,
            // which is when the old instance-level state was reset.
            if (Interlocked.Increment(ref probeCalls) == 2)
                await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return "same";
        }, "probe");

        var model = new ScriptedModel(otherStarted);
        using var client = CreateClient(model);

        var stuck = client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "stuck")],
            new ChatOptions { Tools = [probe] });

        // Wait until the stuck run is parked inside its second tool call, then start another run.
        while (Volatile.Read(ref probeCalls) < 2)
            await Task.Delay(10);
        var other = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "other")], new ChatOptions());
        Assert.AreEqual("other done", other.Text);

        var response = await stuck;
        Assert.AreEqual("stuck done", response.Text);
        Assert.IsTrue(model.NudgeSeen,
            $"the third identical call should carry the loop-break nudge; the other run's start must not reset it");
    }

    [TestMethod]
    public async Task EachCall_StartsWithFreshLoopTracking()
    {
        var probe = AIFunctionFactory.Create((int x) => "same", "probe");
        var model = new ScriptedModel(otherStarted: null, identicalCalls: 2);
        using var client = CreateClient(model);

        // Two identical calls in one run, then two more in the next: never three in a row in
        // one run, so neither run is nudged.
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "stuck")], new ChatOptions { Tools = [probe] });
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "stuck")], new ChatOptions { Tools = [probe] });

        Assert.IsFalse(model.NudgeSeen);
    }

    private static RockBotFunctionInvokingChatClient CreateClient(IChatClient model) =>
        new(model, progressNotifier: null, toolCallLog: null, new ModelBehavior(),
            new LlmCostEstimator(
                Options.Create(new LlmPricingOptions
                {
                    ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json")
                }),
                NullLogger<LlmCostEstimator>.Instance),
            new StubWorkingMemory(), Options.Create(new AgentHostOptions()), NullLogger.Instance);

    /// <summary>
    /// The "stuck" conversation calls <c>probe</c> with the same argument
    /// <paramref name="identicalCalls"/> times, then answers; "other" answers at once and
    /// signals <paramref name="otherStarted"/>.
    /// </summary>
    private sealed class ScriptedModel(TaskCompletionSource? otherStarted, int identicalCalls = 3) : IChatClient
    {
        private volatile bool _nudgeSeen;

        public bool NudgeSeen => _nudgeSeen;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var first = list.First(m => m.Role == ChatRole.User).Text;
            if (first == "other")
            {
                otherStarted?.TrySetResult();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "other done")));
            }

            var results = list.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
            if (results.Any(r => r.Result?.ToString()?.Contains(Nudge, StringComparison.Ordinal) == true))
                _nudgeSeen = true;

            if (results.Count < identicalCalls)
            {
                var call = new FunctionCallContent(Guid.NewGuid().ToString("N"), "probe",
                    new Dictionary<string, object?> { ["x"] = 1 });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stuck done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
}
