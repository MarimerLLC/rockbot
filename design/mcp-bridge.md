# MCP Bridge Design

## Overview

The MCP Bridge moves MCP tool execution out of the agent host process and into a separate deployable service. Agents communicate with the bridge exclusively via the message bus, enforcing RockBot's core isolation principle.

## Architecture

```
Agent Host                MCP Bridge (separate process)
    │                           │
    │  ToolInvokeRequest        │
    │  topic: tool.invoke.mcp   │
    │ ─────────────────────────>│
    │                           │──> MCP Server (stdio/SSE)
    │                           │<── CallToolResult
    │  ToolInvokeResponse       │
    │  topic: tool.result.{agent}│
    │ <─────────────────────────│
```

Each agent has its own MCP Bridge instance, scoped to that agent's tool set. The bridge is not shared across agents.

## Message Flow

### Tool Discovery

1. Bridge starts and reads `mcp.json`
2. Connects to each configured MCP server
3. Calls `tools/list`, applies allow/deny filters
4. Publishes `McpToolsAvailable` on `tool.meta.mcp.{agentName}`
5. Agent host receives message, registers tools in local `IToolRegistry`

### Tool Invocation

1. LLM emits tool_use for an MCP tool
2. Agent host's `McpToolProxy` publishes `ToolInvokeRequest` to `tool.invoke.mcp`
3. Bridge receives request, routes to correct MCP server
4. Bridge publishes `ToolInvokeResponse` (or `ToolError`) to `tool.result.{agentName}`
5. Agent host receives response, returns to LLM as `tool_result`

### Metadata Refresh

Agent publishes `McpMetadataRefreshRequest` to `tool.meta.mcp.refresh`. Bridge re-runs `tools/list` and publishes updated `McpToolsAvailable`.

### Config File Changes

Bridge watches `mcp.json` via `FileSystemWatcher`. On change, it disconnects removed servers, connects new ones, and publishes updated tool availability.

## Content Trust

Every tool message carries an `rb-content-trust` header:

| Value | Meaning |
|---|---|
| `tool-request` | Outbound tool invocation request |
| `tool-output` | Data returned from an external tool (UNTRUSTED) |

Tool responses are always rendered as `tool_result` content blocks, never as user text or system instructions.

## Configuration (mcp.json)

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "mcp-server-filesystem",
      "args": ["/data"],
      "allowedTools": ["read_file", "list_directory"]
    },
    "database": {
      "type": "sse",
      "url": "http://mcp-db:8080/sse",
      "deniedTools": ["drop_table"]
    }
  }
}
```

## Timeout Strategy

- **Bridge timeout** (default 30s): CancellationToken on MCP server call. Publishes `ToolError` with `Code: "timeout"` and `IsRetryable: true`.
- **Agent timeout** (default 60s): Timer on the proxy side. Synthesizes timeout error locally if no response arrives.
- Bridge timeout < agent timeout ensures proper error propagation.

## Projects

| Project | Role |
|---|---|
| `RockBot.Tools.Mcp` | Agent-side proxy (`McpToolProxy`, `McpToolsAvailableHandler`) + bridge-side executor |
| `RockBot.Tools.Mcp.Bridge` | Standalone worker service hosting the bridge |
| `RockBot.Messaging.Abstractions` | `WellKnownHeaders` constants |
