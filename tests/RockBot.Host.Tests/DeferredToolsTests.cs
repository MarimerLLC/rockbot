using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

/// <summary>
/// #670: rarely used tool families stay out of interactive tool lists until the session loads them
/// with <c>get_tool_guide</c> or calls one of their tools by name.
/// </summary>
[TestClass]
public class DeferredToolsTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name, "does " + name);

    private sealed class CallerScopedTool(AIFunction inner) : DelegatingAIFunction(inner), ICallerScopedTool
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ICallerScopedTool) ? this : base.GetService(serviceType, serviceKey);
    }

    private static ChatOptions FullOptions() => new()
    {
        Tools =
        [
            Tool("save_memory"), Tool("edit_memory"), Tool("delete_memory"), Tool("update_memory_importance"),
            Tool("get_tool_guide"), Tool("file_read"), Tool("spawn_subagent"),
            Tool("spawn_wisps"), Tool("spawn_workers"), Tool("schedule_task"), Tool("invoke_agent"),
        ],
    };

    private static List<string> Names(ChatOptions options) => options.Tools!.Select(t => t.Name).ToList();

    private static string NewSession() => "s-" + Guid.NewGuid().ToString("N");

    [TestMethod]
    public void Partition_HoldsBackDeferredFamilies_KeepsTheRest()
    {
        var options = FullOptions();
        using var _ = DeferredToolContext.Set(new DeferredToolActivations(), NewSession());

        var deferred = DeferredToolContext.Partition(options);

        Assert.AreEqual(7, deferred);
        CollectionAssert.AreEqual(
            new[] { "save_memory", "get_tool_guide", "file_read", "spawn_subagent" }, Names(options));
    }

    [TestMethod]
    public void Orientation_ListsEveryHeldBackFamilyWithItsTools()
    {
        var options = FullOptions();
        using var _ = DeferredToolContext.Set(new DeferredToolActivations(), NewSession());
        DeferredToolContext.Partition(options);

        var text = DeferredToolContext.Orientation()!;

        StringAssert.StartsWith(text, DeferredToolContext.OrientationHeading);
        StringAssert.Contains(text, "- wisp: spawn_wisps");
        StringAssert.Contains(text, "- memory: edit_memory, delete_memory, update_memory_importance");
        StringAssert.Contains(text, "- a2a: invoke_agent");
        Assert.IsFalse(text.Contains("register_agent"), "only tools the run actually holds are named");
    }

    [TestMethod]
    public void GuideCall_LoadsTheFamily_AndItStaysLoadedOnTheNextTurn()
    {
        var store = new DeferredToolActivations();
        var session = NewSession();

        var turn1 = FullOptions();
        using (DeferredToolContext.Set(store, session))
        {
            DeferredToolContext.Partition(turn1);
            Assert.AreEqual(1, DeferredToolContext.Observe(turn1, "get_tool_guide", "wisp"));
            Assert.AreEqual("spawn_wisps", Names(turn1)[^1]);
            Assert.AreEqual(0, DeferredToolContext.Observe(turn1, "get_tool_guide", "web"), "not a deferred family");
        }

        var turn2 = FullOptions();
        using (DeferredToolContext.Set(store, session))
        {
            DeferredToolContext.Partition(turn2);
            DeferredToolContext.Observe(turn2, "get_tool_guide", "scheduling");
        }

        CollectionAssert.AreEqual(
            new[] { "save_memory", "get_tool_guide", "file_read", "spawn_subagent", "spawn_wisps", "schedule_task" },
            Names(turn2),
            "loaded families come back in load order, appended after the core tools");
    }

    [TestMethod]
    public void CallByName_LoadsTheFamilyAndReturnsTheTool()
    {
        var options = FullOptions();
        using var _ = DeferredToolContext.Set(new DeferredToolActivations(), NewSession());
        DeferredToolContext.Partition(options);

        var tool = DeferredToolContext.TryActivate(options, "delete_memory");

        Assert.AreEqual("delete_memory", tool?.Name);
        CollectionAssert.IsSubsetOf(new[] { "edit_memory", "delete_memory", "update_memory_importance" }, Names(options));
        Assert.IsNull(DeferredToolContext.TryActivate(options, "no_such_tool"));
    }

    [TestMethod]
    public void NoStore_DefersNothing()
    {
        var options = FullOptions();
        using var _ = DeferredToolContext.Set(null, NewSession());

        Assert.AreEqual(0, DeferredToolContext.Partition(options));
        Assert.AreEqual(11, options.Tools!.Count);
        Assert.IsNull(DeferredToolContext.Orientation());
    }

    [TestMethod]
    public void CallerScopedTool_IsNeverDeferred()
    {
        var options = new ChatOptions { Tools = [Tool("get_tool_guide"), new CallerScopedTool(Tool("spawn_wisps")), Tool("schedule_task")] };
        using var _ = DeferredToolContext.Set(new DeferredToolActivations(), NewSession());

        DeferredToolContext.Partition(options);

        CollectionAssert.AreEqual(new[] { "get_tool_guide", "spawn_wisps" }, Names(options));
    }

    [TestMethod]
    public void ListWithoutTheLoader_DefersNothing_AndGetsNoFamilies()
    {
        var store = new DeferredToolActivations();
        var session = NewSession();
        store.Activate(session, "wisp");
        var options = new ChatOptions { Tools = [Tool("file_read"), Tool("spawn_wisps"), Tool("schedule_task")] };
        var evaluator = new ChatOptions { Tools = [Tool("judge")] };
        using var _ = DeferredToolContext.Set(store, session);

        Assert.AreEqual(0, DeferredToolContext.Partition(options));
        Assert.AreEqual(3, options.Tools!.Count);
        Assert.AreEqual(0, DeferredToolContext.AddActivated(evaluator));
        CollectionAssert.AreEqual(new[] { "judge" }, Names(evaluator));
    }

    [TestMethod]
    public void IdleSession_ForgetsItsFamilies()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new DeferredToolActivations(time) { IdleTimeout = TimeSpan.FromHours(4) };
        store.Activate("s", "wisp");

        time.Advance(TimeSpan.FromHours(3));
        CollectionAssert.AreEqual(new[] { "wisp" }, store.Get("s").ToList());

        time.Advance(TimeSpan.FromHours(5));
        Assert.AreEqual(0, store.Get("s").Count);
    }

    [TestMethod]
    public async Task ChatClient_LoadsAFamilyBeforeTheLoopDispatches()
    {
        var options = FullOptions();
        var inner = new CallingChatClient(
            new FunctionCallContent("c1", "get_tool_guide", new Dictionary<string, object?> { ["name"] = "worker" }),
            new FunctionCallContent("c2", "schedule_task"));
        var sut = new TypedToolSurfaceChatClient(inner);

        using var _ = DeferredToolContext.Set(new DeferredToolActivations(), NewSession());
        DeferredToolContext.Partition(options);
        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        CollectionAssert.IsSubsetOf(new[] { "spawn_workers", "schedule_task" }, Names(options),
            "the guide call loads its family; a direct call to a held-back tool is rescued");
    }

    [TestMethod]
    public void GuideNameFromJson_ReadsTheNameArgument()
    {
        Assert.AreEqual("a2a", DeferredToolContext.GuideNameFromJson("{\"name\":\"a2a\"}"));
        Assert.IsNull(DeferredToolContext.GuideNameFromJson("not json"));
        Assert.IsNull(DeferredToolContext.GuideNameFromJson("{\"other\":1}"));
    }

    [TestMethod]
    public void Catalog_ToolNamesAreUnique()
    {
        var all = DeferredToolFamilies.All.SelectMany(f => f.Tools).ToList();
        Assert.AreEqual(all.Count, all.Distinct().Count());
    }

    private sealed class CallingChatClient(params FunctionCallContent[] calls) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls])));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
