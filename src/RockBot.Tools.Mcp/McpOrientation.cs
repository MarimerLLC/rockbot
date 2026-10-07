using System.Text;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// The short MCP orientation each run's context carries (#614, porting mcp-aggregator#48): what
/// MCP servers are, how their typed tools are named, the workflow for the run's
/// <see cref="TypedToolMode"/>, the <c>mcp/{server}</c> skill to read first, and the escape hatch.
/// <para>
/// It deliberately says nothing a tool schema already says — the meta-tools' own descriptions
/// carry their parameters — and stays well under <see cref="MaxLength"/>. The full reference is
/// the <c>mcp</c> tool guide (<see cref="McpToolSkillProvider"/>), fetched on demand. The text is
/// fixed per mode, so the prompt-cache prefix holds within a tier.
/// </para>
/// </summary>
internal static class McpOrientation
{
    /// <summary>The ceiling a test holds every mode's orientation to.</summary>
    public const int MaxLength = 2000;

    private const string Invoke = "mcp_invoke_tool";

    public static string Build(TypedToolMode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TypedToolSurfaceContext.OrientationHeading);
        sb.AppendLine("MCP servers connect you to live, personal and external systems: email, calendar, files, " +
                      "APIs and more. When a request needs data or actions like that, use them rather than " +
                      "answering from memory.");

        if (mode != TypedToolMode.Off)
        {
            sb.AppendLine("- A server's tools are typed tools named `{server}__{tool}`, e.g. " +
                          "`calendar-mcp__get_events`. Call them like any other tool, with arguments that " +
                          "match their schema.");
        }

        sb.AppendLine(mode switch
        {
            TypedToolMode.Eager =>
                "- Every server's tools are already in your tool list. `mcp_list_services` shows which servers " +
                "exist and what each is for.",
            TypedToolMode.Lazy =>
                $"- Typed tools join your list as you need them. If the one you want isn't there, call " +
                $"`{McpTypedToolSurface.FindToolsName}` with a few keywords: its matches become callable by " +
                "typed name, so call one next.",
            TypedToolMode.Pinned =>
                $"- Typed tools join your list as you need them. If the one you want isn't there, call " +
                $"`{McpTypedToolSurface.FindToolsName}` with a few keywords: its matches become callable by " +
                "typed name, so call one next. Once you call a server, all its typed tools stay in your list.",
            _ =>
                "- Pick a server with `mcp_list_services`, read its tools with `mcp_get_service_details`, then " +
                $"call one with `{Invoke}`: `tool_name` is the server's own tool name and the tool's " +
                "parameters go inside `arguments`."
        });

        sb.AppendLine("- Before using a server for the first time, load its `mcp/{server}` skill with `get_skill` " +
                      "when one exists: it records argument shapes and pitfalls already verified.");

        if (mode != TypedToolMode.Off)
        {
            sb.AppendLine($"- Escape hatch: `{Invoke}` reaches any server tool. Its `tool_name` is the server's " +
                          "own tool name, never a `{server}__{tool}` name, and the tool's parameters go inside " +
                          "`arguments`.");
        }

        sb.AppendLine("- An unknown-server or unknown-tool result lists what does exist; choose from it rather " +
                      "than guessing.");
        sb.Append("- Full reference: `get_tool_guide` with name `mcp`.");
        return sb.ToString();
    }
}
