using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// #757: <see cref="RockBotFunctionInvokingChatClient"/> is a per-tier singleton that concurrent
/// runs share, so a per-request iteration override (<see cref="MaxIterationsOverrideContext"/>)
/// must apply to that request alone. It must not change the shared instance, and it must not
/// leak into another call running on the same client.
/// </summary>
[TestClass]
public class FunctionInvokingIterationBudgetTests
{
    private const int DefaultBudget = 5;

    [TestMethod]
    public async Task OverlappingCalls_EachRunUnderItsOwnBudget()
    {
        var aParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var aCalls = 0;
        var probeA = AIFunctionFactory.Create(async (int x) =>
        {
            if (Interlocked.Increment(ref aCalls) == 1)
            {
                aParked.TrySetResult();
                await releaseA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return $"a{x}";
        }, "probe");

        var bCalls = 0;
        var probeB = AIFunctionFactory.Create(async (int x) =>
        {
            var n = Interlocked.Increment(ref bCalls);
            if (n == 1)
                bStarted.TrySetResult();
            else if (n == 2)
                await aDone.Task.WaitAsync(TimeSpan.FromSeconds(10)); // B is still looping when A ends
            return $"b{x}";
        }, "probe");

        var model = new ScriptedModel();
        using var client = CreateClient(model);

        Task<ChatResponse> a;
        using (MaxIterationsOverrideContext.Set(2))
            a = client.GetResponseAsync([new ChatMessage(ChatRole.User, "A")], new ChatOptions { Tools = [probeA] });
        await aParked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var b = client.GetResponseAsync([new ChatMessage(ChatRole.User, "B")], new ChatOptions { Tools = [probeB] });
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseA.TrySetResult();
        var aResponse = await a.WaitAsync(TimeSpan.FromSeconds(10));
        aDone.TrySetResult();
        var bResponse = await b.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("A done", aResponse.Text);
        Assert.AreEqual("B done", bResponse.Text);
        Assert.AreEqual(2, model.ToolRequests("A"), "A runs under its own override of 2");
        Assert.AreEqual(1, model.ToollessRequests("A"), "A ends with one request offering no tools");
        Assert.AreEqual(DefaultBudget, model.ToolRequests("B"), "B keeps the default budget despite A's override");
        Assert.AreEqual(1, model.ToollessRequests("B"));
        Assert.AreEqual(2, aCalls);
        Assert.AreEqual(DefaultBudget, bCalls);

        // The calls finished out of order (A, then B). Nothing stale may be left behind: a fresh
        // call without an override still gets the default budget.
        var probeC = AIFunctionFactory.Create((int x) => $"c{x}", "probe");
        var cResponse = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "C")], new ChatOptions { Tools = [probeC] });
        Assert.AreEqual("C done", cResponse.Text);
        Assert.AreEqual(DefaultBudget, model.ToolRequests("C"), "no stale budget left on the shared client");
    }

    [TestMethod]
    public async Task Override_NeverChangesSharedInstance()
    {
        var model = new ScriptedModel();
        using var client = CreateClient(model);
        var constructed = client.MaximumIterationsPerRequest;

        var seenInsideTool = new ConcurrentBag<int>();
        var probe = AIFunctionFactory.Create((int x) =>
        {
            seenInsideTool.Add(client.MaximumIterationsPerRequest);
            return $"r{x}";
        }, "probe");

        using (MaxIterationsOverrideContext.Set(3))
        {
            var overridden = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "over")], new ChatOptions { Tools = [probe] });
            Assert.AreEqual("over done", overridden.Text);
        }

        Assert.AreEqual(3, model.ToolRequests("over"));
        Assert.AreEqual(3, seenInsideTool.Count);
        Assert.IsTrue(seenInsideTool.All(v => v == constructed),
            "the override must not write the shared instance's limit");
        Assert.AreEqual(constructed, client.MaximumIterationsPerRequest);

        var plain = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "plain")], new ChatOptions { Tools = [probe] });
        Assert.AreEqual("plain done", plain.Text);
        Assert.AreEqual(DefaultBudget, model.ToolRequests("plain"), "a later call without an override runs the default budget");
        Assert.AreEqual(1, model.ToollessRequests("plain"));
    }

    [TestMethod]
    public async Task BudgetReached_FinalRequestCarriesNoTools_AndLaterCallsAreNotRun()
    {
        var probeCalls = 0;
        var probe = AIFunctionFactory.Create((int x) =>
        {
            Interlocked.Increment(ref probeCalls);
            return $"r{x}";
        }, "probe");

        // The model asks for a tool even when the request offers none.
        var model = new ScriptedModel(ignoreMissingTools: true);
        using var client = CreateClient(model);

        ChatResponse response;
        using (MaxIterationsOverrideContext.Set(3))
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "stubborn")], new ChatOptions { Tools = [probe] })
                .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(3, probeCalls, "the call returned by the tool-less final request must not run");
        Assert.AreEqual(3, model.ToolRequests("stubborn"));
        Assert.IsTrue(model.ToollessRequests("stubborn") >= 1, "the final request offers no tools");

        var notRun = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Last();
        StringAssert.Contains(notRun.Result?.ToString(), "Not run");
    }

    private static RockBotFunctionInvokingChatClient CreateClient(IChatClient model) =>
        new(model, progressNotifier: null, toolCallLog: null,
            new ModelBehavior { MaxToolIterationsOverride = DefaultBudget },
            new LlmCostEstimator(
                Options.Create(new LlmPricingOptions
                {
                    ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json")
                }),
                NullLogger<LlmCostEstimator>.Instance),
            new StubWorkingMemory(), Options.Create(new AgentHostOptions()), NullLogger.Instance);

    /// <summary>
    /// Keys each conversation on its first user text. A request offering tools gets a
    /// <c>probe</c> call; one offering none gets "{name} done" (or, with
    /// <paramref name="ignoreMissingTools"/>, another <c>probe</c> call).
    /// </summary>
    private sealed class ScriptedModel(bool ignoreMissingTools = false) : IChatClient
    {
        private readonly ConcurrentDictionary<string, int> _toolRequests = new();
        private readonly ConcurrentDictionary<string, int> _toollessRequests = new();

        public int ToolRequests(string name) => _toolRequests.GetValueOrDefault(name);
        public int ToollessRequests(string name) => _toollessRequests.GetValueOrDefault(name);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var name = list.First(m => m.Role == ChatRole.User).Text;
            var hasTools = options?.Tools is { Count: > 0 };
            (hasTools ? _toolRequests : _toollessRequests).AddOrUpdate(name, 1, (_, n) => n + 1);

            if (hasTools || ignoreMissingTools)
            {
                var results = list.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
                var call = new FunctionCallContent(Guid.NewGuid().ToString("N"), "probe",
                    new Dictionary<string, object?> { ["x"] = results });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{name} done")));
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
