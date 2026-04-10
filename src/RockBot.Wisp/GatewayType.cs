using System.Text.Json.Serialization;

namespace RockBot.Wisp;

/// <summary>
/// The tool backend that a direct step routes through.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GatewayType>))]
public enum GatewayType
{
    /// <summary>MCP server tool invocation via mcp_invoke_tool.</summary>
    Mcp,

    /// <summary>A2A agent invocation via invoke_agent.</summary>
    A2A,

    /// <summary>Script execution via execute_{language}_script.</summary>
    Script,

    /// <summary>Web tools: web_search or web_browse.</summary>
    Web
}
