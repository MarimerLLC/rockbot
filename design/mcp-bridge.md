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

### Typed wrapper tools (`{server}__{tool}`)

Issue #420, ported from mcp-aggregator PR #42.

**Mode.** `McpBridge:WrapperMode` (Helm `agent.mcpWrapperMode`) chooses how downstream tools are offered:
- `Off` (default): the six `mcp_*` management tools only.
- `Eager`: `McpWrapperCatalog` also registers one typed tool per downstream tool.
- `Lazy`: the same typed tools, but only in the sessions that search for them. See [Lazy typed tools](#lazy-typed-tools-mcp_find_tools) below.

The aggregator measured typed tools at 90–100% first-call success for small models, against 10–25% through `invoke_tool`. Per-tier defaults are chosen in #613.

**Naming.**
- Names are `{server}__{tool}`. Each part is sanitised to `[A-Za-z0-9_-]`, the strictest charset the LLM providers accept. That is narrower than MCP's charset, so `.` becomes `-`.
- Names are never truncated. A name over 64 characters isn't registered, and the tool stays reachable through `mcp_invoke_tool`.
- Routing uses the server and tool stored with the registration (`ToolRegistration.DownstreamName`), never a parse of the name.

**Registration.** Each wrapper gets:
- the downstream input schema as its parameter schema, unchanged;
- the description `[server] <downstream description>`;
- source `mcp:{server}`.

The catalog reconciles on every `McpServersIndexed`:
- new tools are registered;
- vanished tools, or tools whose fingerprint (description plus canonical schema) moved, are replaced;
- unchanged tools keep their registration.

**Collisions.**
- The server prefix keeps same-named tools on different servers apart.
- Two tools on one server that sanitise to the same name: the first is kept.
- A wrapper never displaces a tool registered by anything else.
- Operator-denied tools never get a wrapper, because the bridge's tool list is already filtered.

**One call path.**
- A wrapper call checks for missing required keys first. If any are missing, it returns them with the schema, without calling the downstream. Nothing else is checked.
- It then takes exactly the path `mcp_invoke_tool` takes (`McpManagementExecutor.InvokeDownstreamAsync`): proxy, bridge (guards, attachments, elicitation, timeouts, reconnect-and-retry, error hints), then recovery.
- Calls are counted in `rockbot.mcp.tool.invocations`, tagged `via=wrapper|invoke_tool`.
- `mcp_invoke_tool` stays available as the escape hatch.

**Profiles, workers and wisps.**
- Every tool profile admits wrappers, the same profiles that admit `mcp_invoke_tool`.
- A worker's `tools_allow` narrows wrappers through their dotted `{server}.{tool}` form, so `"calendar-mcp.*"` admits that server's typed tools.
- Wisp `Direct/Mcp` steps keep routing through `mcp_invoke_tool`. `McpStepValidator` finds a wrapper's schema by its `DownstreamName`.

### Lazy typed tools (`mcp_find_tools`)

Issue #612, ported from mcp-aggregator PR #42's lazy mode. Eager mode puts every downstream tool in every prompt. Lazy mode keeps the baseline to the `mcp_*` tools and adds a typed tool only to the session that asked for it. The aggregator measured the trade-off: mid-tier models used 7.6k input tokens per task lazy, against 4.3k eager and 10.3k through `invoke_tool`. Weak models drop more calls on the extra discovery step (gpt-4.1-nano: 60% lazy, 90% eager). #613 measures this on RockBot's tiers.

**Indexing.** `McpWrapperCatalog` builds the same wrappers it would in eager mode, under the same naming and collision rules, but does not register them in `IToolRegistry`. The registry is global, and one session's search must not grow every other session's context. That is why the aggregator rejected process-wide activation.

**Activation.** A session activates typed tools in three ways:
- **`mcp_find_tools(query, limit=10)`** ranks typed tools with the aggregator's scoring (`McpToolSearch`). An exact match of the whole query against the typed or tool name scores +1000. Each query token then scores +80 if it equals a tool-name token, else +50 if it is inside the tool name, else +30 if it is inside the typed name. Each token also adds +10 if the description has it and +5 if the server's name, display name or summary does. Every match comes back with its typed name, server, `serverId`, tool name, description, full `inputSchema` and an `activated` flag, and is activated. There is no "schemas without activating" variant: a schema the model can't call by name is `invoke_tool` with extra steps.
- **`mcp_get_service_details(server)`** activates that server's typed tools. With `tool_name`, it activates just that one.
- **A call by typed name** to a valid tool the list doesn't hold (a name from a skill or from memory) runs it and activates it. It does not answer "unknown tool".

`mcp_find_tools` is registered only in lazy mode, under source `mcp:management`, so every profile that has the gateway has it.

**Scope and lifetime.** `McpTypedToolSurface` holds the activations, keyed by the tool session id that the run's registry tools carry. That id is the one their executors see: `session/{id}` for a conversation, or the subagent's, worker's or wisp's own namespace.
- Activations last for the session, up to `McpBridge:MaxActivatedToolsPerSession` (default 40). Beyond that, the oldest is dropped. Re-activating a tool keeps its place, so the tool list's order, and the provider's prompt cache, stay stable.
- A session's activations are dropped after `McpBridge:ActivationIdleTimeout` (default 12h) without use.
- When a server is removed, or a tool's fingerprint or server id changes, the catalog evicts that tool from every session. The next search activates it again, with its new schema.

**Taking effect within the turn.** The tool array is built once per message, so activations reach a run through `AgentLoopRunner`, the single LLM entry point:
- `RunAsync` sets the surface as an ambient context (`TypedToolSurfaceContext`) and adds the session's earlier activations to the run's tool list.
- On the native path, `TypedToolSurfaceChatClient` sits directly under `RockBotFunctionInvokingChatClient`. Before each request, it adds the tools activated since the last one. After each response, it resolves calls to typed tools the list doesn't hold. Both mutate the `ChatOptions` the function-invoking loop dispatches against. `FunctionInvokingChatClient` (M.E.AI 10.x) looks tools up in `options.Tools` live at every dispatch, so a tool added there is callable in the same loop.
- The text-based loop does the same before each request and at its tool lookups.

An added tool carries the same tool session id as the run's other registry tools (`ISessionBoundTool`), and is wrapped like the `mcp_find_tools` entry in the list (for example, with `ChunkingAIFunction`). A run whose tool list has no `mcp_find_tools` never gains typed tools this way. That keeps profiles that leave out the gateway closed.

**Workers.** A worker's `tools_allow` doesn't gate lazy activations, because workers always keep the gateway and could reach the same tool through `mcp_invoke_tool`.

**Hints.** With typed tools on, `ServiceSearchIndex` sets an MCP candidate's top items to the typed names that best fit the query. It falls back to the server's first tools. So the per-turn service hints and `search_known_services` name typed tools. The completion re-prompt does the same:
- its service hints name typed tools, and in lazy mode activate them first;
- its capability-denial nudge points to `mcp_find_tools` (lazy) or to typed tools (eager), not to `mcp_invoke_tool`.

`search_known_services` stays the per-service router across MCP servers and A2A agents. `mcp_find_tools` is the per-tool search within MCP.

### Metadata Refresh

Agent publishes `McpMetadataRefreshRequest` to `tool.meta.mcp.refresh`. Bridge re-runs `tools/list` and publishes updated `McpToolsAvailable`.

### Operator entries and model registrations

Issue #603. `mcp_register_server` and `mcp_unregister_server` are LLM-callable, and the bridge persists what they do to `mcp.json`. So they may only add servers, and remove the servers they added. An operator's entry is never theirs to change.

**Ownership.** Each entry carries an `origin`:
- **Agent-owned:** `mcp_register_server` stamps `"origin": "agent"` on what it creates.
- **Operator-owned:** every other entry. That covers entries seeded from `McpBridge:DefaultServers`, entries written into `mcp.json` by hand, and entries from before #603, which have no `origin`.
- **Policy makes it the operator's:** an agent-registered entry that the operator has since given any policy belongs to the operator from then on (`McpBridgeServerConfig.IsAgentOwned`). Policy here means anything `mcp_register_server` can't express: tool filters, headers, auth, arg guards, elicitation, attachments, `toolTimeoutMs`, or a `transportMode` other than `auto`.

**What the model can affect:**

| Field | Through `mcp_register_server` | Through `mcp_unregister_server` |
|---|---|---|
| name, `type`, `url`, `command`, `args`, `env` | Sets them on a **new** entry only | Removes an agent-owned entry |
| `allowedTools`, `deniedTools`, `headers`, `auth`, `argGuards`, `elicitation`, `attachments`, `toolTimeoutMs`, `transportMode` | Never. A new entry gets the `McpBridge` defaults. | Never. An entry with any of these is operator-owned and can't be removed. |
| `id`, `origin` | Assigned by the bridge | n/a |

**Rules:**
- **Register only adds.** `mcp_register_server` refuses an existing name, whoever owns it. Replacing an entry would drop its policy, and could re-point a trusted name, credentials included, at a URL of the model's choosing. To move one of its own servers, the model unregisters it first.
- **Unregister only removes the agent's own.** `mcp_unregister_server` refuses an operator-owned entry, and reports an unknown name as unknown.
- **No way around it.** The two paths that used to shed policy, re-registering a name and unregistering then registering it, are both closed for operator entries.
- **Credentials don't follow a URL.** A new registration at the same URL as an operator entry is a separate entry with no headers or auth. Credentials are never copied from one entry to another.
- **Load-time dedup.** It never drops an operator entry in favour of an agent-registered duplicate.
- **The operator changes their own entries through configuration:** `mcp.json` on the volume, or Helm values for seeded servers. A config reload picks the change up.

**Other write paths.** The agent can't write `mcp.json` any other way. The file tools resolve every path under `FileSystem:BasePath` (`/rockbot/shared`) and reject any that resolve outside it. `mcp.json` lives on the agent's data volume (`/data/agent`). Scripts run in the separate scripts-manager pod.

There is no model-facing reconnect. The reconnect sweep and config reloads handle that, and tests use the internal `McpBridgeService.ReconnectAsync`.

### Config File Changes

Bridge watches `mcp.json` via `FileSystemWatcher` (including `Renamed`/`FileName` events so rename-into-place writes are seen) **and** a polling fallback that stats the file's last-write time + size every `ConfigPollIntervalSeconds` (default 5 s, 0 disables). The poll exists because `FileSystemWatcher`/inotify can miss changes entirely on some network/overlay filesystems such as Longhorn PVCs (issue #470). Both paths funnel through a single debounced reload that disconnects removed servers, connects new ones, and publishes updated tool availability. The on-disk stamp is recorded after each load so the bridge's own writes (seeding/dedup) don't trigger a redundant reload.

### Server identity and surface change detection

Ported from mcp-aggregator PRs #42 and #47.

- **Stable id.** Every `mcp.json` entry carries an immutable `id` (12 hex characters).
  - The bridge assigns one to any entry without it on load, and persists it.
  - It is published in `McpServerSummary.ServerId` and returned by `mcp_list_services` and `mcp_get_service_details`.
  - Names stay human-readable. A reconnect keeps the id. A rename (unregister, then register under a new name) gets a new id.
  - Anything that must follow a server durably stores the id next to the name.
- **Who may change an entry.** See [Operator entries and model registrations](#operator-entries-and-model-registrations).
- **Name rules for new registrations.** `mcp_register_server` only accepts names matching `^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$` that don't contain `__`, because server names become part of skill names, file paths and typed tool names. Existing entries that break these rules still load, with a warning.
- **Fingerprints** (`McpSurfaceFingerprint`, SHA-256).
  - **Per-tool fingerprint:** covers name, description and the canonical input schema.
  - **Per-server fingerprint:** covers all tools plus all prompts, including each prompt's arguments.
  - Canonicalisation sorts object keys at every level and keeps array order, so a server that only reorders keys doesn't look changed. A description-only change does count as a change.
  - If a prompt list can't be read, the server fingerprint is `null` (unknown). A failed read is never recorded as a surface. A server without the prompts capability, or one answering method-not-found, simply has no prompts.
- **Refresh.**
  - The bridge re-reads a server's tools and prompts on its existing connection in two cases: when the server sends `notifications/tools/list_changed` or `notifications/prompts/list_changed` (debounced), and every `SurfaceRefreshIntervalSeconds` (default 300) on the reconnect sweep. The periodic refresh covers servers that never send `list_changed`, such as stateless HTTP servers, which have no stream to send it on.
  - Only a moved fingerprint is published.
  - On the agent side, `ToolSchemaCache` is invalidated only when a server's fingerprint or id moves, not on every re-publish.
- **Summary reuse.** Every reconnect and config reload goes through `ConnectServerAsync`. When the fingerprint and the server's self-reported identity are unchanged, the previous LLM-generated summary is reused instead of being regenerated.

### Per-server state and concurrency

These paths run independently of one another:
- the tool-invoke, management and metadata-refresh subscriptions;
- the reconnect sweep;
- config reload;
- list_changed refreshes.

All of the bridge's per-server state lives in `McpServerConnections` (issue #604):

- **Configured servers** map a name to the latest `McpBridgeServerConfig`, connected or not. A configured server that isn't connected is what the reconnect sweep retries.
- **Connected servers** map a name to an immutable `ConnectedServer` snapshot holding the client, config, filtered tools, prompts, metadata, summary, elicitation coordinator and attachment gateway, all from the same connect.
  - A reader takes one snapshot and uses it for the whole operation, so it can't see a new client with an old tool list.
  - A surface refresh publishes a new snapshot of the same connection.
- **One writer per server at a time.** Connect, refresh and disconnect each hold that server's lock, so the reconnect sweep, a config reload and an invoke's reconnect-and-retry queue behind one another instead of each building a client.
  - The retry passes the client that failed. If another caller has already replaced it, the retry uses the new connection instead of reconnecting again.
  - The sweep skips a server that got connected while it waited.
- **Leases.** A call leases its snapshot. A replaced or removed connection is *retired*, and its client and attachment HTTP client are disposed when the last lease is released. They are never disposed under a running call.
  - Before #604, a reconnect disposed the client while calls were in flight, and those calls failed with a bogus "timed out after 60000ms" error.
- **Failed reconnect with a changed config.** If every reconnect attempt fails after the config changed, the previous connection keeps serving under the new config, with its tool list filtered again by the new filters. That can only narrow what's callable, so an operator who just denied a tool isn't overruled by a server that happened to be down.

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
      "id": "3f9c1a7e2b40",
      "type": "sse",
      "url": "http://mcp-db:8080/sse",
      "deniedTools": ["drop_table"]
    }
  }
}
```

`id` is assigned by the bridge when absent. Don't hand-edit it.

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
