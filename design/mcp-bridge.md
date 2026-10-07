# MCP Bridge Design

## Overview

The MCP Bridge owns RockBot's connections to MCP servers. It runs as `McpBridgeService`, a hosted service inside the RockBot.Agent process (`src/RockBot.Agent/McpBridge/`); there is no separate bridge deployable. The agent side (`RockBot.Tools.Mcp`, registered with `AddMcpToolProxy`) still talks to it only through the message bus, so the topics below are the contract between the two halves.

## Architecture

```
Agent side                McpBridgeService (same process)
    │                           │
    │  ToolInvokeRequest        │
    │  topic: tool.invoke.mcp   │
    │ ─────────────────────────>│
    │                           │──> MCP Server (HTTP/SSE)
    │                           │<── CallToolResult
    │  ToolInvokeResponse       │
    │  topic: tool.result.{agent}│
    │ <─────────────────────────│
```

Each agent process hosts its own bridge, scoped to that agent's `mcp.json`. The bridge is not shared across agents.

## Message Flow

### Tool Discovery

1. Bridge starts and reads `mcp.json` (`McpBridge:ConfigPath`), seeding any `McpBridge:DefaultServers` entries
2. Connects to each configured MCP server
3. Lists its tools and prompts, applies allow/deny filters, and writes an LLM summary of the server
4. Publishes `McpServersIndexed` on `tool.meta.mcp.{agentName}`
5. The agent's `McpServersIndexedHandler` updates `McpServerIndex`. On the first message it registers the six `mcp_*` management tools in `IToolRegistry` (plus `mcp_find_tools` when a tier is `Lazy` or `Pinned`), and `McpWrapperCatalog` reconciles the typed tools. Downstream tools are not registered under their bare names.

### Tool Invocation

1. LLM calls a typed `{server}__{tool}` tool or `mcp_invoke_tool`
2. `McpManagementExecutor.InvokeDownstreamAsync` hands it to `McpToolProxy`, which publishes `ToolInvokeRequest` to `tool.invoke.mcp` with the server in the `rb-mcp-server` header
3. Bridge receives request, routes to correct MCP server
4. Bridge publishes `ToolInvokeResponse` (or `ToolError`) to `tool.result.{agentName}`
5. The proxy matches the response by correlation id; it passes through `McpRecoveryExecutor` and returns to the LLM as `tool_result`

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

**Mode.** `McpBridge:WrapperMode` (Helm `agent.mcpWrapperMode`) chooses how downstream tools are offered, and `McpBridge:WrapperModeByTier:{Low|Balanced|High}` (Helm `agent.mcpWrapperModeByTier`) overrides it per model tier. See [Per-tier modes](#per-tier-modes-and-pinned-servers) below.
- `Off`: the six `mcp_*` management tools only.
- `Eager`: `McpWrapperCatalog` also registers one typed tool per downstream tool.
- `Lazy`: the same typed tools, but only in the sessions that search for them. See [Lazy typed tools](#lazy-typed-tools-mcp_find_tools) below.
- `Pinned` (default): lazy, plus every typed tool of a server the session has called.

The aggregator measured typed tools at 90–100% first-call success for small models, against 10–25% through `invoke_tool`. #613 measured the modes on RockBot's own tiers; see [Per-tier modes](#per-tier-modes-and-pinned-servers).

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

`mcp_find_tools` is registered when any tier's mode is `Lazy` or `Pinned`, under source `mcp:management`, so every profile that has the gateway has it. Runs in an `Off` or `Eager` tier drop it (see [Per-tier modes](#per-tier-modes-and-pinned-servers)).

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

### Per-tier modes and pinned servers

Issue #613. The right surface depends on the model: a weak model gets typed tools wrong less often when they are all in front of it, while a strong one handles the discovery step and saves the context. RockBot routes each message to a tier, so the mode follows the tier.

**Defaults.** Every tier defaults to `Pinned`. The measurement is in [docs/measurements/2026-10-07-mcp-wrapper-modes.md](../docs/measurements/2026-10-07-mcp-wrapper-modes.md):
- Pinned matched or beat lazy on every tier.
- Eager was the most accurate and the cheapest, but every typed tool goes in every request. The main agent's own 56 tools plus production's 92 typed tools would break the providers' 128-tool cap.
- Off was the least reliable and the most expensive.

**Configuration.** `McpToolSurfaceOptions.ModeFor(tier)` is `WrapperModeByTier[tier]`, else `WrapperMode`. The registry is global, so it holds what any tier needs:
- typed tools are indexed when any tier is not `Off`;
- they are registered in `IToolRegistry` when any tier is `Eager`, so `tools_allow`, tool profiles and the worker and wisp registry lookups keep working unchanged;
- `mcp_find_tools` is registered when any tier is `Lazy` or `Pinned`.

**Per-run shaping.** `AgentLoopRunner.RunAsync` already knows the run's tier. It makes the tier's mode ambient (`TypedToolSurfaceContext.Set(surface, tier)`), and `TypedToolSurfaceContext.Shape` fits the run's tool list to it before anything else:

| Run's mode | Dropped from the list | Then |
|---|---|---|
| `Off` | registered typed tools and `mcp_find_tools` | — |
| `Eager` | `mcp_find_tools` | — |
| `Lazy` / `Pinned` | registered typed tools | the session's activations join, as above |

The re-prompt hints, the capability-denial nudge, `ServiceSearchIndex`'s typed top items and the "now callable" note in `mcp_get_service_details` read the run's mode, not a global one. Activations are recorded whenever some tier activates, so a session that moves between tiers keeps them.

**Pinned servers.** In `Pinned`, a session that calls one of a server's tools — typed or through `mcp_invoke_tool`, successfully or not — keeps every typed tool of that server in its list from the next request on. It targets the case lazy mode handles worst: a conversation that goes back to the same server for a different tool, where lazy needs another search.
- `McpManagementExecutor.InvokeDownstreamAsync`, the one downstream path, calls `McpTypedToolSurface.Pin` after every call. Pins are recorded only when some tier is pinned, and only for a server with typed tools, so an invented server name can't push a real one out.
- A session keeps its `McpBridge:MaxPinnedServersPerSession` (default 3) most recently called servers. Pinned tools come after the session's activations, in the server's order, so the list grows at the end and the prompt cache holds.
- Pinned tools share the activations' budget (`MaxActivatedToolsPerSession`, default 40). The activations come first, then the most recently called server's tools, then the next server's, until the budget is spent.
- Whatever the mode, typed tools join a run's list only while it holds at most `McpBridge:MaxToolsPerRequest` tools (default 120). OpenAI and Azure reject a request with more than 128 tools, and the loop appends its task-list tools after the typed ones.
- Pins share the activations' lifetime: they go with the session after `ActivationIdleTimeout`, and a removed server's tools drop out because the list is rebuilt from the catalog each time.

### Orientation and server instructions

Issue #614, ported from mcp-aggregator#48.

- **Orientation.** Every run whose tool list holds an MCP tool carries a short orientation: `AgentLoopRunner.EnsureMcpOrientation` inserts it as a system message right after the system prompt, from `ITypedToolSurface.Orientation(mode)`. `McpOrientation` builds one fixed text per mode, so the prompt-cache prefix holds within a tier. It covers what MCP servers are for, the `{server}__{tool}` naming, the mode's workflow, the `mcp/{server}` skill to read first and the `mcp_invoke_tool` escape hatch, and stays under 2,000 characters (test-enforced). It repeats nothing the tool schemas already say.
- **Full reference.** The `mcp` tool guide (`McpToolSkillProvider`), fetched with `get_tool_guide`.
- **Server instructions.** A downstream server's own `instructions` are capped by `McpInstructionsCap`: 2,000 characters in `mcp_get_service_details` output, 8,000 in the prompt that writes the server's summary. Longer text is cut on a line boundary and ends with an explicit `[Server instructions truncated: N of M characters shown. ...]` marker, never a silent cut. The marker names the server's guide-like tools, if it has any, as the way to the rest.

### Metadata Refresh

Agent publishes `McpMetadataRefreshRequest` to `tool.meta.mcp.refresh` (`McpStartupProbeService` does so once the agent has started). Bridge reconnects the named server, or every connected one, and publishes updated `McpServersIndexed`. Requests sent before the bridge finished starting are ignored.

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
      "type": "streamable-http",
      "url": "http://mcp-files:8080/mcp",
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

Only HTTP servers connect. An entry with `command`/`args`/`env` (stdio) still parses, but the bridge skips it with a warning: it runs inside the agent and doesn't launch server processes.

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

- **Bridge timeout**: CancellationToken on the MCP server call. The proxy sends its request timeout in the `rb-timeout-ms` header (`McpToolProxy:RequestTimeoutSeconds`, default 60s); the bridge caps it at `McpBridge:MaxTimeoutMs` (default 900s) and falls back to `McpBridge:DefaultTimeoutMs` (default 60s) without one. A server's `toolTimeoutMs` overrides both, still capped at `MaxTimeoutMs`. On expiry the bridge publishes `ToolError` with `Code: "timeout"` and `IsRetryable: true`.
- **Agent timeout**: the proxy waits `McpToolProxy:ResponseTimeoutSeconds` (RockBot.Agent default 930s) and synthesizes a timeout error locally if no response arrives.
- The proxy outwaits the bridge's cap, so the caller sees the bridge's own timeout error rather than a transport failure.

## Projects

| Project | Role |
|---|---|
| `RockBot.Tools.Mcp` | Agent side: `McpToolProxy`, `McpServersIndexedHandler`, `McpManagementExecutor`, typed tools (`McpWrapperCatalog`, `McpTypedToolSurface`), `McpOrientation`; plus types the bridge shares (`McpServersIndexed`, management messages, `McpCallDiagnostics`, elicitation) |
| `RockBot.Agent` (`McpBridge/`) | The bridge: `McpBridgeService` (hosted service), `McpServerConnections`, `mcp.json` config, arg guards, attachments, auth |
| `RockBot.Messaging.Abstractions` | `WellKnownHeaders` constants |
