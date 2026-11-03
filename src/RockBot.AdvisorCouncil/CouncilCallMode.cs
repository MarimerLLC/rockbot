using System.Text.Json.Nodes;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RockBot.AdvisorCouncil;

/// <summary>
/// Tells the <c>advise</c> tool whether this call runs as a Tasks-extension task, which decides
/// how it may ask its clarifying question.
/// </summary>
/// <remarks>
/// <para>
/// A copy of the research server's <c>ResearchCallMode</c> (src/RockBot.ResearchAgent): keep the
/// two in step. Inside a task a question must go through <c>ElicitAsync</c> (the task store routes
/// it: status <c>input_required</c>, answered with <c>tasks/update</c>); SDK 2.2.0 fails a task
/// whose tool throws <see cref="InputRequiredException"/>. Outside a task the stateless server
/// can't send <c>elicitation/create</c>, so the tool asks with MRTR instead.
/// </para>
/// <para>
/// The SDK does not tell a tool which way it is running, so this repeats the Tasks filter's own
/// test: the selector allows a task (ours returns <see cref="McpTaskExecutionMode.Optional"/>),
/// the request is on the 2026-07-28 protocol or later, and the client opted into the tasks
/// extension in its capabilities or the request's <c>_meta</c>. The end-to-end tests in
/// <c>RockBot.AdvisorCouncil.Tests</c> pin both modes.
/// </para>
/// </remarks>
internal static class CouncilCallMode
{
    /// <summary>The selector the server registers with <c>WithTasks</c>.</summary>
    public static McpTaskExecutionMode Select(RequestContext<CallToolRequestParams> request) =>
        McpTaskExecutionMode.Optional;

    /// <summary>The first protocol revision with Tasks and MRTR.</summary>
    internal const string July2026ProtocolVersion = "2026-07-28";

    /// <summary>Revisions are dates, so they compare ordinally.</summary>
    internal static bool IsJuly2026OrLater(string? protocolVersion) =>
        protocolVersion is not null && string.CompareOrdinal(protocolVersion, July2026ProtocolVersion) >= 0;

    /// <summary>The per-request client capabilities key in <c>_meta</c> (2026-07-28).</summary>
    private const string MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities";

    /// <summary>True when the client says it can answer elicitation, in the session's capabilities or the request's own <c>_meta</c>.</summary>
    public static bool ClientCanElicit(RequestContext<CallToolRequestParams> request)
    {
        if (request.Server?.ClientCapabilities?.Elicitation is not null)
            return true;
        var rpc = request.JsonRpcRequest;
        if (rpc?.Context?.ClientCapabilities?.Elicitation is not null)
            return true;
        return rpc?.Params?["_meta"]?[MetaClientCapabilities]?["elicitation"] is JsonObject;
    }

    /// <summary>True when the Tasks extension runs this call as a task.</summary>
    public static bool IsTask(RequestContext<CallToolRequestParams> request)
    {
        var rpc = request.JsonRpcRequest;
        if (rpc is null)
            return false;
        if (!IsJuly2026OrLater(rpc.Context?.ProtocolVersion))
            return false;
        if (rpc.Context?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true)
            return true;
        return rpc.Params?["_meta"]?[MetaClientCapabilities]?["extensions"] is JsonObject extensions
               && extensions.ContainsKey(TasksProtocol.ExtensionId);
    }
}
