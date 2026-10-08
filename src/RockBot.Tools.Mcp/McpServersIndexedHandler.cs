using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Handles <see cref="McpServersIndexed"/> messages from the MCP Bridge.
/// On the first message, registers the 8 MCP management tools in <see cref="IToolRegistry"/>, and
/// keeps <c>mcp_answer</c> registered exactly while a connected server hands questions back.
/// All subsequent messages only update the <see cref="McpServerIndex"/> cache and
/// invalidate any cached tool schemas for the affected servers.
/// </summary>
public sealed class McpServersIndexedHandler(
    IToolRegistry registry,
    McpServerIndex index,
    McpManagementExecutor executor,
    ILogger<McpServersIndexedHandler> logger,
    ToolSchemaCache? schemaCache = null,
    McpWrapperCatalog? wrappers = null) : IMessageHandler<McpServersIndexed>
{
    public async Task HandleAsync(McpServersIndexed message, MessageHandlerContext context)
    {
        // Every reconnect and config reload re-publishes a server's summary; only a moved
        // fingerprint (or a server whose surface is unknown) means its cached schemas are stale.
        // Lookups re-fetch lazily.
        if (schemaCache is not null)
        {
            var previous = index.Servers.ToDictionary(s => s.ServerName, StringComparer.OrdinalIgnoreCase);
            foreach (var server in message.Servers)
            {
                if (!SurfaceUnchanged(previous.GetValueOrDefault(server.ServerName), server))
                    schemaCache.Invalidate(server.ServerName);
            }
            foreach (var removed in message.RemovedServers)
                schemaCache.Invalidate(removed);
        }

        index.Apply(message);

        logger.LogInformation(
            "MCP server index updated: {Added} added/updated, {Removed} removed",
            message.Servers.Count, message.RemovedServers.Count);

        if (!index.ManagementToolsRegistered)
        {
            RegisterManagementTools();
            index.ManagementToolsRegistered = true;
        }

        SyncAnswerTool();

        // Typed {server}__{tool} tools follow the index; a no-op unless some tier's wrapper mode is on.
        if (wrappers is not null)
            await wrappers.ApplyAsync(message, context.CancellationToken);
    }

    internal static bool SurfaceUnchanged(McpServerSummary? previous, McpServerSummary incoming) =>
        previous is { Fingerprint: { } before }
        && incoming.Fingerprint is { } after
        && before == after
        && previous.ServerId == incoming.ServerId;

    /// <summary>
    /// <c>mcp_answer</c> is offered only while some connected server can hand a question back,
    /// so every other run's tool list stays as it was (#613).
    /// </summary>
    private void SyncAnswerTool()
    {
        var wanted = index.Servers.Any(s => s.Handback);
        var registered = registry.GetExecutor(McpHandbackContext.AnswerToolName) is not null;

        if (wanted && !registered)
        {
            registry.Register(new ToolRegistration
            {
                Name = McpHandbackContext.AnswerToolName,
                Description = AnswerToolDescription,
                ParametersSchema = AnswerToolSchema,
                Source = "mcp:management"
            }, executor);
            logger.LogInformation("Registered {Tool}: a connected MCP server hands questions back", McpHandbackContext.AnswerToolName);
        }
        else if (!wanted && registered)
        {
            registry.Unregister(McpHandbackContext.AnswerToolName);
            logger.LogInformation("Unregistered {Tool}: no connected MCP server hands questions back", McpHandbackContext.AnswerToolName);
        }
    }

    internal const string AnswerToolDescription =
        "Answer a question an MCP server asked in the middle of a tool call. When a tool result says the server " +
        "\"needs input\" and gives a question_id, the call is paused, not finished: answering resumes it, and this " +
        "tool returns the call's result (or the server's next question). Answer from what you know when you can. " +
        "When you can't, ask the user and call mcp_answer after they reply. A yes/no, confirm or approve field is " +
        "the user's decision: always ask, never decide it yourself. Pass decline: true to refuse; the server then " +
        "continues without the value. Each question is answered once, from the conversation that caused it, and " +
        "expires after the time it states.";

    internal const string AnswerToolSchema =
        """{"type":"object","properties":{"question_id":{"type":"string","description":"The question_id from the hand-back, exactly as given."},"answers":{"type":"object","description":"Field values keyed by the server's field names, e.g. {\"mailbox\": \"work\"}. Values must fit the field types the question lists.","additionalProperties":true},"decline":{"type":"boolean","description":"Refuse the question instead of answering it."}},"required":["question_id"]}""";

    private void RegisterManagementTools()
    {
        registry.Register(new ToolRegistration
        {
            Name = "mcp_list_services",
            Description = "List all connected MCP servers with their summaries and tool counts. Call this first when you need live, personal, or external data (calendar, email, files, etc.) and don't know which server to use. Before a server's first use, read its mcp/{server} skill (get_skill) if one exists.",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_get_service_details",
            Description = "Get tool details (name, description, parameter schema) for an MCP server. Pass tool_name to get details for one specific tool (preferred — avoids returning all tool schemas). Omit tool_name to list all tools on the server.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"tool_name":{"type":"string","description":"Optional: return details for this specific tool only. Recommended when you know the tool name."}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_invoke_tool",
            Description = "Execute a tool on an MCP server to access live external data or perform actions (e.g. read calendar events, send email, query files). This is the gateway to all external MCP capabilities. Requires server_name and tool_name from mcp_list_services/mcp_get_service_details.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"tool_name":{"type":"string","description":"Name of the tool to invoke"},"arguments":{"type":"object","description":"Arguments to pass to the tool (as a JSON object)"}},"required":["server_name","tool_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_register_server",
            Description = "Register a new MCP server at runtime via SSE transport. Only adds a server under a new name: it can't change or replace one that already exists.",
            ParametersSchema = """{"type":"object","properties":{"name":{"type":"string","description":"Unique server name"},"type":{"type":"string","enum":["sse"],"description":"Transport type"},"url":{"type":"string","description":"SSE endpoint URL"}},"required":["name","type","url"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_unregister_server",
            Description = "Remove an MCP server at runtime. Only servers added with mcp_register_server can be removed; servers the operator configured can't.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server to remove"}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_get_prompt",
            Description = "Invoke a prompt template on an MCP server. Returns filled-in messages (user/assistant) ready to use as context or instructions. Use mcp_get_service_details to see available prompt templates and their argument schemas first.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"prompt_name":{"type":"string","description":"Name of the prompt template to invoke"},"arguments":{"type":"object","description":"Key-value arguments to fill in the prompt template (all values as strings)"}},"required":["server_name","prompt_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_list_resources",
            Description = "List the resources and resource templates an MCP server exposes: readable data (documents, files, records) addressed by URI rather than called like a tool. mcp_list_services shows which servers have any. Returns each one's uri (or uriTemplate), name, description and MIME type.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_read_resource",
            Description = "Read one resource from an MCP server by its URI, as mcp_list_resources lists it; for a uriTemplate, fill in every {…} expression first. Also follows a [resource link] in a tool result. Text comes back inline; binary or very large content is saved to the shared volume and comes back as a file path.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"uri":{"type":"string","description":"The resource URI as the server lists it, any template expanded (e.g. 'docs://files/readme.md')"}},"required":["server_name","uri"]}""",
            Source = "mcp:management"
        }, executor);

        // Lazy typed tools (#612): the search that activates them, registered when some tier is
        // lazy or pinned; runs of other tiers drop it (#613). Same source as the other gateway
        // tools, so every tool profile that has the gateway has this too.
        if (wrappers is { Options.ActivatesWrappers: true })
        {
            registry.Register(new ToolRegistration
            {
                Name = McpTypedToolSurface.FindToolsName,
                Description = McpFindToolsExecutor.Description,
                ParametersSchema = McpFindToolsExecutor.ParametersSchema,
                Source = "mcp:management"
            }, new McpFindToolsExecutor(wrappers.Surface, index));

            logger.LogInformation("Registered {Tool} (typed MCP tools: {Modes})",
                McpTypedToolSurface.FindToolsName, wrappers.Options.Describe());
        }

        logger.LogInformation("Registered 8 MCP management tools");
    }
}
