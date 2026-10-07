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

### Invoke pre-checks and error hints

The bridge answers mistakes it can prove itself, with the real names, so the model can correct the call on its next attempt (`McpCallDiagnostics`, ported from mcp-aggregator PRs #29, #36, #42 and #51):

- **Unknown server.** The error lists the registered servers. A registered server whose connection is down gets a separate "not reachable right now" message.
- **Unknown tool.** Only tools the server lists *after* the operator's `allowedTools`/`deniedTools` filter may be called. Any other name is refused before reaching the downstream, with the available names. That also means a filtered-out tool can't be called by name. On a miss against the cached list the bridge re-lists once, so a tool added since the last connect still goes through.
- **Unknown prompt / missing required prompt arguments.** These are refused before reaching the downstream, with the prompt names or the full argument signature.
- **A downstream protocol rejection** (`-32602` invalid params, `-32601` method not found). The server answered, so the connection is alive: the error goes back to the model without the reconnect-and-retry that other exceptions get.

When a call fails, a hint is appended **only on positive evidence**:

1. **Missing required keys or unrecognised keys.** The hint lists them, together with the keys sent and the input schema. A missing field the downstream already named in words recovery understands is left to `McpRecoveryExecutor`.
2. **A top-level value whose JSON type contradicts the declared `type`.** For example: "Parameter 'to' is declared as array but you sent a string."
3. **A narrow validation phrase from another SDK** (`Invalid arguments for tool`, `-32602`, `validation error for`). The schema is attached without a type claim.
4. **The C# SDK's detail-free `An error occurred invoking '<tool>'.`** This gets a hedged note: the problem is either in a nested value or format, or on the server's side.

Any other failure on schema-valid arguments comes back unchanged. A hint that blames the arguments for a server-side failure sends the model hunting for a bug that doesn't exist (mcp-aggregator#50).

### Metadata Refresh

Agent publishes `McpMetadataRefreshRequest` to `tool.meta.mcp.refresh`. Bridge re-runs `tools/list` and publishes updated `McpToolsAvailable`.

### Config File Changes

Bridge watches `mcp.json` via `FileSystemWatcher` (including `Renamed`/`FileName` events so rename-into-place writes are seen) **and** a polling fallback that stats the file's last-write time + size every `ConfigPollIntervalSeconds` (default 5 s, 0 disables). The poll exists because `FileSystemWatcher`/inotify can miss changes entirely on some network/overlay filesystems such as Longhorn PVCs (issue #470). Both paths funnel through a single debounced reload that disconnects removed servers, connects new ones, and publishes updated tool availability. The on-disk stamp is recorded after each load so the bridge's own writes (seeding/dedup) don't trigger a redundant reload.

## Content Trust

Every tool message carries an `rb-content-trust` header:

| Value | Meaning |
|---|---|
| `tool-request` | Outbound tool invocation request |
| `tool-output` | Data returned from an external tool (UNTRUSTED) |

Tool responses are always rendered as `tool_result` content blocks, never as user text or system instructions.

## Elicitation (server-to-client questions)

An MCP server handling `tools/call` may send the bridge an `elicitation/create` request and
block until it is answered. The bridge answers every one, inside the caller's tool-call budget,
under a per-server policy (`elicitation` in mcp.json, default `McpBridge:DefaultElicitation`):
form-mode questions are answered from configured defaults and then a responder, with the answer
validated against the server's own schema; credential-shaped fields, url mode, and anything past
the per-call cap are declined. What was asked is appended to the tool result so the agent can
supply the value on the next call.

`sampling/createMessage` is not implemented, so that capability is never advertised.

See `design/mcp-elicitation.md`.

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

## Protocol versions (MCP C# SDK 2.x)

The bridge uses the 2.x SDK, which speaks the 2026-07-28 protocol: no `initialize` handshake
(`server/discover` instead), no Streamable HTTP sessions, and Multi Round-Trip Requests (MRTR)
for server-to-client questions. The bridge does not set `McpClientOptions.ProtocolVersion`, so
the client prefers 2026-07-28 and falls back automatically to the `initialize` handshake for
servers that do not support it — external servers on SDK 1.x keep working unchanged. Pinning a
version would disable that fallback, so don't.

Transport selection is unchanged: `"type": "sse"` / `"http"` / `"streamable-http"` all mean an
HTTP server, and `transportMode` (default `auto`) picks Streamable HTTP or legacy SSE. 2.x
servers turn their legacy SSE endpoints off by default, so a `transportMode: "sse"` entry only
works against servers that still offer SSE; leave it at `auto` unless a server needs it.

The in-repo servers (`McpServer.*`) are 2.x too, and so stateless by default. None of them uses
server-to-client requests, progress notifications or per-session state, so nothing depends on
the sessions stateless mode removes.

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
