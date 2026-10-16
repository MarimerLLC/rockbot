using System.Text;
using Microsoft.Extensions.AI;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="AgentLoopRunner.AppendMcpServiceHint"/> (#712): the re-prompt's service hint names
/// typed tools but never activates them, since a changed tool list misses the prompt cache. In lazy
/// mode it tells the model to load them first.
/// </summary>
[TestClass]
public class AgentLoopRunnerServiceHintTests
{
    private sealed class FakeSurface(TypedToolMode mode) : ITypedToolSurface
    {
        public TypedToolMode ModeFor(ModelTier tier) => mode;
        public string LoaderToolName => "mcp_find_tools";
        public bool IsTypedTool(string toolName) => toolName.Contains("__", StringComparison.Ordinal);
        public string? Orientation(TypedToolMode m) => null;
        public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode m) => [];
        public AIFunction? ActivateByName(string toolSessionId, string toolName) =>
            throw new AssertFailedException("the re-prompt hint must not activate tools");
    }

    private static readonly ServiceSearchCandidate Calendar = new()
    {
        Id = "calendar",
        Type = "mcp",
        Summary = "Calendar access",
        TopItems = ["calendar__get_events", "calendar__create_event"],
        TopItemsAreTypedTools = true,
    };

    private static string Hint(TypedToolMode mode, ServiceSearchCandidate candidate)
    {
        var sb = new StringBuilder();
        using (TypedToolSurfaceContext.Set(new FakeSurface(mode), ModelTier.Balanced))
            AgentLoopRunner.AppendMcpServiceHint(sb, candidate, "mcp_find_tools");
        return sb.ToString();
    }

    [TestMethod]
    [DataRow(TypedToolMode.Lazy)]
    [DataRow(TypedToolMode.Pinned)]
    public void ActivatingModes_NameTheTools_AndSayToLoadThemFirst(TypedToolMode mode)
    {
        var hint = Hint(mode, Calendar);

        StringAssert.Contains(hint, "calendar__get_events, calendar__create_event");
        StringAssert.Contains(hint, "load them first with mcp_find_tools");
        StringAssert.Contains(hint, "mcp_get_service_details(server_name=\"calendar\")");
        Assert.IsFalse(hint.Contains("call them directly", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Eager_SaysToCallThemDirectly()
    {
        var hint = Hint(TypedToolMode.Eager, Calendar);

        StringAssert.Contains(hint, "call them directly");
        Assert.IsFalse(hint.Contains("mcp_find_tools", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Off_ListsSampleTools_AndPointsAtServiceDetails()
    {
        var hint = Hint(TypedToolMode.Off, Calendar with { TopItems = ["get_events"], TopItemsAreTypedTools = false });

        StringAssert.Contains(hint, "Sample tools: get_events");
        StringAssert.Contains(hint, "mcp_get_service_details(server_name=\"calendar\")");
    }
}
