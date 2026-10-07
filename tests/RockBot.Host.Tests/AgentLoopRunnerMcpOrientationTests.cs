using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="AgentLoopRunner.EnsureMcpOrientation"/> (#614): the run's MCP orientation sits right
/// behind the system prompt, once, in the run's mode, and only when the run has MCP tools.
/// </summary>
[TestClass]
public class AgentLoopRunnerMcpOrientationTests
{
    private sealed class FakeSurface : ITypedToolSurface
    {
        public TypedToolMode Mode { get; init; } = TypedToolMode.Pinned;
        public TypedToolMode ModeFor(ModelTier tier) => Mode;
        public string LoaderToolName => "mcp_find_tools";
        public bool IsTypedTool(string toolName) => toolName.Contains("__", StringComparison.Ordinal);
        public string? Orientation(TypedToolMode mode) => $"{TypedToolSurfaceContext.OrientationHeading}\nmode={mode}";
        public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode) => [];
        public AIFunction? ActivateByName(string toolSessionId, string toolName) => null;
    }

    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    private static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.System, "You are RockBot."),
        new(ChatRole.System, "Skill index: ..."),
        new(ChatRole.User, "What's on my calendar?")
    ];

    private static IEnumerable<ChatMessage> Orientations(List<ChatMessage> messages) =>
        messages.Where(m => m.Role == ChatRole.System
            && m.Text.StartsWith(TypedToolSurfaceContext.OrientationHeading, StringComparison.Ordinal));

    [TestMethod]
    public void WithMcpTools_InsertsTheOrientationRightBehindTheSystemPrompt()
    {
        var messages = Conversation();
        var options = new ChatOptions { Tools = [Tool("mcp_list_services"), Tool("get_skill")] };

        using (TypedToolSurfaceContext.Set(new FakeSurface(), ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        Assert.AreEqual(4, messages.Count);
        Assert.AreEqual("You are RockBot.", messages[0].Text);
        Assert.AreEqual($"{TypedToolSurfaceContext.OrientationHeading}\nmode=Pinned", messages[1].Text);
        Assert.AreEqual("Skill index: ...", messages[2].Text);
    }

    [TestMethod]
    public void RunTwiceOnTheSameList_LeavesOneCopy_InTheLatestRunsMode()
    {
        var messages = Conversation();
        var options = new ChatOptions { Tools = [Tool("mcp_list_services")] };

        using (TypedToolSurfaceContext.Set(new FakeSurface { Mode = TypedToolMode.Pinned }, ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);
        using (TypedToolSurfaceContext.Set(new FakeSurface { Mode = TypedToolMode.Off }, ModelTier.Low))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        var orientation = Orientations(messages).Single();
        StringAssert.EndsWith(orientation.Text, "mode=Off");
        Assert.AreSame(orientation, messages[1]);
    }

    [TestMethod]
    public void TypedToolsAlone_CountAsMcpTools()
    {
        var messages = Conversation();
        var options = new ChatOptions { Tools = [Tool("calendar__get_events")] };

        using (TypedToolSurfaceContext.Set(new FakeSurface(), ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        Assert.AreEqual(1, Orientations(messages).Count());
    }

    [TestMethod]
    public void WithoutMcpTools_AddsNothing_AndRemovesAStaleCopy()
    {
        var messages = Conversation();
        messages.Insert(1, new ChatMessage(ChatRole.System, $"{TypedToolSurfaceContext.OrientationHeading}\nstale"));
        var options = new ChatOptions { Tools = [Tool("get_skill"), Tool("web_search")] };

        using (TypedToolSurfaceContext.Set(new FakeSurface(), ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        Assert.AreEqual(0, Orientations(messages).Count());
        Assert.AreEqual(3, messages.Count);
    }

    [TestMethod]
    public void WithoutASurface_AddsNothing()
    {
        var messages = Conversation();
        var options = new ChatOptions { Tools = [Tool("mcp_list_services")] };

        using (TypedToolSurfaceContext.Set(null, ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        Assert.AreEqual(0, Orientations(messages).Count());
    }

    [TestMethod]
    public void WithoutASystemPrompt_GoesFirst()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var options = new ChatOptions { Tools = [Tool("mcp_list_services")] };

        using (TypedToolSurfaceContext.Set(new FakeSurface(), ModelTier.Balanced))
            AgentLoopRunner.EnsureMcpOrientation(messages, options);

        Assert.AreSame(Orientations(messages).Single(), messages[0]);
    }
}
