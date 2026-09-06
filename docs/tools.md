---
title: Tools
nav_order: 8
---

# Tools subsystem

Tools are the agent's interface to the external world. Every side-effecting action — web
search, file operation, API call, script execution, MCP invocation — flows through the tool
system. The design keeps the agent process free of direct dependencies on external services;
it invokes tools by name, and the infrastructure routes and executes them.

---

## Tool execution model

```
LLM produces a tool call (name + JSON arguments)
    │
    ▼
UserMessageHandler (in agent process)
    │   RegistryToolFunction wraps each registered tool as an AIFunction
    │
    ▼
IToolRegistry.GetExecutor(name)
    │
    ▼
IToolExecutor.ExecuteAsync(ToolInvokeRequest)
    │
    ├── Local executor (web, scheduling, in-process MCP)
    │       Returns ToolInvokeResponse directly
    │
    └── Remote executor (MCP proxy, script runner)
            Publishes ToolInvokeRequest to message bus topic
            Awaits correlated ToolInvokeResponse (or ToolError) via ReplyTo
```

All tools are registered in `IToolRegistry` at startup by hosted service registrars. The agent
never hard-codes tool names — it discovers them at runtime via the registry and exposes them to
the LLM as `AIFunction` instances.

---

## Core abstractions (`RockBot.Tools.Abstractions`)

### `IToolRegistry`

Central directory of registered tools and their executors:

```csharp
public interface IToolRegistry
{
    IReadOnlyList<ToolRegistration> GetTools();
    IToolExecutor? GetExecutor(string name);
    void Register(ToolRegistration registration, IToolExecutor executor);
    void Unregister(string name);
}
```

`ToolRegistry` is a thread-safe `ConcurrentDictionary`-backed implementation. Attempting to
register a duplicate tool name throws — enforcing uniqueness across all tool providers.

### `ToolRegistration`

Metadata the LLM receives for each tool:

```csharp
public sealed record ToolRegistration(
    string Name,              // e.g. "web_search", "mcp_invoke_tool"
    string Description,       // Natural-language description for the LLM
    string ParametersSchema,  // JSON Schema string (OpenAI function-calling format)
    string Source             // Backend type, e.g. "brave", "mcp:weather-server"
);
```

`ToLlmToolDefinition()` converts a `ToolRegistration` to the `AITool` type expected by
`Microsoft.Extensions.AI`.

### `IToolExecutor`

```csharp
public interface IToolExecutor
{
    Task<ToolInvokeResponse> ExecuteAsync(
        ToolInvokeRequest request,
        CancellationToken ct = default);
}
```

### `ToolInvokeRequest` / `ToolInvokeResponse` / `ToolError`

```csharp
public sealed record ToolInvokeRequest(
    string ToolCallId,   // Correlates LLM tool call to response
    string ToolName,
    string Arguments,    // JSON string of the tool's arguments
    string SessionId
);

public sealed record ToolInvokeResponse(
    string ToolCallId,
    string ToolName,
    string Content,      // Tool output (plain text or JSON)
    bool IsError
);

public sealed record ToolError(
    string ToolCallId,
    string ToolName,
    string Code,         // ToolError.Codes.* constant
    string Message,
    bool IsRetryable
);
```

`ToolError.Codes` constants: `ToolNotFound`, `ExecutionFailed`, `Timeout`, `InvalidArguments`.

---

## Tool guide system

Each tool subsystem can register an `IToolSkillProvider` that publishes a usage guide —
a markdown document explaining how to use the tools effectively.

```csharp
public interface IToolSkillProvider
{
    string Name { get; }       // e.g. "web-tools", "mcp-tool-guide"
    string Summary { get; }    // One-line description
    string GetDocument();      // Full markdown guide
}
```

`ToolGuideTools` exposes two LLM-callable tools built from all registered providers:

| Tool | Purpose |
|---|---|
| `list_tool_guides()` | Lists available guides by name + summary |
| `get_tool_guide(name)` | Returns the full markdown guide for a named provider |

The typical agent workflow:
1. Encounter a new capability (e.g. MCP server)
2. Call `list_tool_guides` to discover available guides
3. Call `get_tool_guide("mcp-tool-guide")` to read the full procedure
4. Follow the guide; call `save_skill` to cache the pattern for future sessions

---

## Tool invoke handler

`ToolInvokeHandler` is an `IMessageHandler<ToolInvokeRequest>` that receives tool invocation
requests from the message bus (topic `tool.invoke`), dispatches to the registered executor,
and publishes the result to the `ReplyTo` topic (default `tool.result`).

Error classification:
- `TimeoutException` → `ToolError` with code `Timeout`, `IsRetryable = true`
- `ArgumentException` → `ToolError` with code `InvalidArguments`, `IsRetryable = false`
- Other exceptions → `ToolError` with code `ExecutionFailed`, `IsRetryable = false`
- Unregistered tool name → `ToolError` with code `ToolNotFound`

Instrumentation: every invocation records an `Activity` (kind Producer) and updates the
`rockbot.tool.invoke.duration` histogram and `rockbot.tool.invocations` counter, tagged by
tool name and result status.

---

## MCP bridge (`RockBot.Tools.Mcp`)

The MCP (Model Context Protocol) bridge connects the agent to external MCP servers — services
that expose tools in a standard protocol over SSE transport.

### Discovery and registration

`McpToolRegistrar` (hosted service) connects to each configured MCP server at startup:

1. For each server in `McpOptions.Servers`, establishes an SSE connection
2. Calls `tools/list` to discover available tools
3. Registers each tool in `IToolRegistry` with source `mcp:{serverName}`
4. Publishes `McpServersIndexed` to notify the agent that the index has changed

`McpStartupProbeService` sends a `McpMetadataRefreshRequest` after the agent is fully started,
closing the race condition where the bridge publishes the inventory before the agent has
subscribed.

### Server configuration

```json
{
  "Mcp": {
    "Servers": [
      {
        "Name": "weather-server",
        "Command": "uvx",
        "Arguments": ["mcp-server-weather"],
        "EnvironmentVariables": { "API_KEY": "..." }
      }
    ]
  }
}
```

### Management tools

When the first `McpServersIndexed` message arrives, `McpServersIndexedHandler` registers five
management tools that give the agent runtime control over MCP servers:

| Tool | Purpose |
|---|---|
| `mcp_list_services` | Lists all connected MCP servers from the local index (no bridge call) |
| `mcp_get_service_details(server_name, tool_name?)` | Returns tool schemas for a server (or a single tool) |
| `mcp_invoke_tool(server_name, tool_name, arguments)` | Invokes a specific MCP tool |
| `mcp_register_server(server_name, command, arguments?)` | Connects a new MCP server at runtime |
| `mcp_unregister_server(server_name)` | Disconnects an MCP server and removes its tools |

**Critical:** `mcp_invoke_tool` requires the exact `server_name` from `mcp_list_services`. The
`rb-mcp-server` header carries the server name through the message bus so `McpToolProxy` routes
to the correct server. Case-insensitive matching is used throughout.

### Tool invocation flow (remote)

When the agent is in a separate process from the MCP bridge:

```
Agent: mcp_invoke_tool(server_name, tool_name, args)
    │
    ▼
McpManagementExecutor → McpToolProxy
    │   Publishes ToolInvokeRequest to "tool.invoke.mcp"
    │   rb-mcp-server: {server_name}
    │
    ▼
McpBridge (tools process)
    │   McpToolExecutor.ExecuteAsync()
    │   → calls MCP server via SSE
    │
    ▼
ToolInvokeResponse on "tool.result.{agentName}"
    │   Correlated by ToolCallId
```

`McpToolProxy` uses lazy subscription initialization (semaphore-protected) so the
result-listener topic is subscribed only on the first actual invocation.

### In-process registration

For agents that embed the MCP bridge in-process (not via message bus):

```csharp
agent.AddMcpTools(opts => builder.Configuration.GetSection("Mcp").Bind(opts));
```

This registers `McpToolRegistrar` and `McpStartupProbeService` directly, skipping the message
bus hop.

### Attachment passthrough

Some MCP servers (calendar, email, drive) take or return file attachments. Passing those bytes
through the LLM as base64 wastes context and confuses the model — and most servers also accept a
"stash + handle" REST flow that the model cannot easily orchestrate by itself. The bridge's
**attachment passthrough** hides both shapes behind a single convention: **the model speaks
paths**, and the bridge translates them into whatever the server expects.

Storage is the shared volume mounted into the agent and script pods at `$ROCKBOT_SHARED_PATH`
(defaults to `/rockbot/shared`). Attachments live under `${ROCKBOT_SHARED_PATH}/attachments/`,
so a script can write a file there and the agent can hand it to an MCP tool without ever
loading the bytes into context.

Opt in per server with an `attachments` block in `mcp.json`:

```json
{
  "mcpServers": {
    "calendar": {
      "type": "sse",
      "url": "https://calendar-mcp.example/",
      "attachments": {
        "thresholdBytes": 262144,
        "uploadFieldName": "file",
        "endpointPath": "/attachments",
        "outbound": {
          "paramPaths": ["attachments[*]"]
        },
        "inbound": {
          "tools": ["get_email_attachment"]
        }
      }
    }
  }
}
```

| Field | Default | Purpose |
|---|---|---|
| `thresholdBytes` | 262144 (256 KB) | Below: outbound files are inlined as `{name, base64Content}`. At or above: uploaded via `POST /attachments` and replaced with `{attachmentId}`. |
| `uploadFieldName` | `file` | Multipart form-field name used for the upload. |
| `endpointPath` | `/attachments` | Path appended to the server URL for `POST` (upload), `GET /{id}` (download), and `DELETE /{id}` (cleanup). |
| `outbound.paramPaths` | _none_ | JSON paths that contain attachment objects to rewrite. First version supports `arrayKey[*]` — a top-level array whose items have a `path` field. |
| `inbound.tools` | _none_ | Tool names that accept the gateway-only `mode: "save"` argument. |

#### Outbound (model attaches a file)

When the model calls e.g. `send_email({ attachments: [{ path: "/rockbot/shared/attachments/report.xlsx" }] })`:

1. Bridge reads bytes from the path via `IAttachmentStorage`.
2. **Below threshold** → replaces the entry with `{ name, base64Content }`.
3. **At or above threshold** → `POST /attachments` (multipart), accepts **200 or 201**,
   replaces the entry with `{ attachmentId }`.

The underlying tool sees a fully populated payload and never knows the gateway was involved.

#### Inbound (model wants a file written to disk)

For tools listed in `inbound.tools`, the model can pass a gateway-only argument
`mode: "save"`. The bridge translates this into `mode: "stash"` (default) or
`mode: "inline"` (when an optional `sizeHint` is below threshold), invokes the tool, and
then materializes the bytes:

- **Stash path** → `GET /attachments/{id}` to fetch the body, write to
  `${ROCKBOT_SHARED_PATH}/attachments/<name>`, fire-and-forget `DELETE /attachments/{id}`.
  A 404 on DELETE is tolerated.
- **Inline path** → base64-decode the response and write to disk.

Either way the agent receives `{ path, name, size, mime }` — a tiny JSON payload it can act on
without seeing the bytes. Filename collisions are resolved with `-2`, `-3`, … suffixes.

Attachment passthrough is a no-op when a server has no `attachments` block. The agent skill
guide instructs the model to pass `{ path: "/rockbot/shared/attachments/<file>" }` rather than
base64 whenever a tool's parameter takes attachments — operators don't need to do anything more
than enable the manifest.

### Argument guards

Some third-party MCP servers resolve path arguments inside their *own* pod: a
`download_file` call with `save_directory: "/tmp"` succeeds — but the file lands in the
server pod's local filesystem, invisible to the agent and script pods, while the tool
reports success. Argument guards let an operator declare per-server validation that the
bridge enforces before forwarding a tool call:

```json
{
  "mcpServers": {
    "onedrive-personal": {
      "type": "sse",
      "url": "http://onedrive-personal:3001/",
      "argGuards": [
        { "handler": "path-prefix",
          "tools": ["download_file"],
          "options": {
            "args": ["save_directory"],
            "allowedPrefixes": ["/rockbot/shared"],
            "requireArgs": true } }
      ]
    }
  }
}
```

| Field | Meaning |
|---|---|
| `handler` | Registry name of the `IMcpArgGuard` implementation. Built-in: `path-prefix`. |
| `tools` | Tool names the rule applies to (case-insensitive). Empty/omitted = all tools. |
| `options` | Handler-specific; for `path-prefix`: `args` (argument names to validate), `allowedPrefixes` (absolute paths), `requireArgs` (reject when a listed argument is missing; default false). |

Behavior:

- Guards run on the model's **original** arguments, before attachment passthrough rewrites
  them, and apply to every caller (primary agent, subagents, workers, wisps, A2A) — all MCP
  invocations flow through the bridge's single invoke handler.
- A rejection returns `ToolError` (`invalid_arguments`, not retryable); the message names the
  offending argument, the allowed prefixes, and the reason, so the model can self-correct in
  one turn.
- **Fail closed**: an unknown `handler` name or invalid `options` refuses the server
  connection entirely (logged as an error). A declared policy that cannot be enforced never
  silently degrades.
- `path-prefix` rejects relative paths and traversal that escapes the prefixes
  (`/rockbot/shared/../../tmp`); comparison is `Ordinal` (Linux semantics) and
  boundary-aware (`/rockbot/shared` does not match `/rockbot/shared-evil`). Missing
  arguments pass unless `requireArgs` is set. Empty `allowedPrefixes` is a config error,
  not allow-all.
- Handlers are resolved from a DI registry by name — mcp.json never names CLR types
  (`register_mcp_server` is model-callable, so config-driven type loading would be a code
  execution channel). Re-registering an existing server name preserves its guards.
- Guards are excluded from the canonical-identity dedup, like `attachments`: they describe
  how the server is invoked, not which server it is.

See `design/mcp-arg-guards.md` for the security rationale.

---

## Multimodal input: `analyze_file`

Attachment passthrough gets a file *onto the shared volume*. `analyze_file` is the other half:
it gets that file *in front of a model* as real image content.

```
analyze_file({ path: "attachments/architecture.png",
               prompt: "Describe the components and how they connect.",
               tier: "high" })
    │
    ▼
AnalyzeFileToolExecutor  (RockBot.Tools.FileSystem)
    │   SafeResolvePath containment under FileSystemOptions.BasePath
    │   extension → MIME, checked against AnalyzeFileMimeTypes
    │   size checked against AnalyzeFileMaxBytes
    │
    ▼
ILlmClient.GetResponseAsync(
    [ ChatMessage(User, [ TextContent(prompt), DataContent(bytes, mime) ]) ], tier, …)
    │
    ▼
ToolInvokeResponse { Content = "Three services arranged left to right. …" }
```

The analysis runs as its own LLM call rather than as content in the agent's own loop. That is
not an implementation shortcut — on OpenAI-compatible APIs, which is every provider RockBot
talks to, tool-role messages accept text only, so bytes can enter a conversation solely as
content parts on a user message. Running the look-up as a side call sidesteps that and keeps
the agent's context free of bytes: a path goes in, prose comes out. The full reasoning, and the
sequencing of the remaining multimodal work, is in `design/multimodal-input.md`.

### Enabling it

`analyze_file` is registered only when a configured tier declares that its model accepts image
input:

```json
{
  "LLM": {
    "High": {
      "ModelId": "openai/gpt-5.5",
      "SupportsImageInput": true
    }
  }
}
```

Or `LLM__High__SupportsImageInput=true` as an environment variable. With no such tier the tool
is not registered and the file-tools skill guide omits it — an agent is never taught a
capability its deployment does not have.

When the requested `tier` is not one that can see, the executor substitutes the nearest tier
that can (High → Balanced → Low). This matters because `ILlmClient` retries a failed Low/High
call on Balanced: sending a vision request to a blind tier would fail twice and report the
second, less informative error.

### Limits

| Option | Default | Purpose |
|---|---|---|
| `FileSystemOptions.AnalyzeFileMaxBytes` | 8 MiB | Refused above this, before any bytes are read. Providers cap the encoded request well above it; the limit keeps a mistake cheap. |
| `FileSystemOptions.AnalyzeFileMimeTypes` | PNG, JPEG, GIF, WebP | The formats every vision-capable provider accepts. Adding `application/pdf` or an audio type is a deployment decision — whether it works depends on the provider behind the tier. |

---

## Service search (`RockBot.ServiceSearch`)

`search_known_services` is a unified BM25 keyword search over all known A2A agents **and**
MCP servers in a single tool call. It removes the need to call `list_known_agents` and
`mcp_list_services` separately when routing a task to the right backend.

### How it works

`ServiceSearchIndex` reads live from two in-memory singletons on every search:

- **`IAgentDirectory`** — all known A2A agents (from live announcements + `well-known-agents.json`), including their LLM-generated summaries, skills, tags, and examples.
- **`McpServerIndex`** — all connected MCP servers (from the bridge index), including their LLM-generated summaries and tool names.

Each source document is flattened into a single text string and ranked with `Bm25Ranker.RankWithScores<T>`. No separate cache or background sync is needed — both sources are already singletons that stay current as agents announce and MCP servers connect.

### Tool

```
search_known_services(query)
```

| Parameter | Required | Description |
|---|---|---|
| `query` | yes | Keywords describing the task (e.g. `"reschedule meeting"`, `"aws spend audit"`) |

Returns up to 5 ranked results:

```json
{
  "results": [
    {
      "id": "SalesOpsAgent",
      "type": "a2a",
      "summary": "Autonomous agent for complex sales workflows and multi-step reporting.",
      "relevance_score": 1.0,
      "top_skills": ["generate-qbr-report", "audit-pipeline"]
    },
    {
      "id": "salesforce-mcp",
      "type": "mcp",
      "summary": "Direct access to Salesforce objects (Accounts, Leads, Opportunities).",
      "relevance_score": 0.72,
      "top_tools": ["search_leads", "update_opportunity"]
    }
  ]
}
```

| Field | Description |
|---|---|
| `id` | Pass to `get_agent_details(agent_name)` or `mcp_get_service_details(server_name)` for full details |
| `type` | `"a2a"` → use `invoke_agent`; `"mcp"` → use `mcp_invoke_tool` |
| `summary` | LLM-generated description — the primary signal for choosing between candidates |
| `relevance_score` | BM25 score normalized to [0, 1]; below ~0.3 consider browsing manually |
| `top_skills` | (A2A only) Top 3 skill IDs — immediate scouting report without a details call |
| `top_tools` | (MCP only) Top 3 tool names — immediate scouting report without a details call |

### Context hints

`AgentContextBuilder` runs `search_known_services` automatically each turn and injects the top
2 matches into the system prompt before the LLM sees the user message:

```
Potentially relevant services for this request (call search_known_services for full search):
- SalesOpsAgent (a2a): Autonomous agent for complex sales workflows, top skills: generate-qbr-report
- salesforce-mcp (mcp): Direct Salesforce CRM access, top tools: search_leads, update_opportunity
```

When the hint already identifies the right service with a high score, the agent can skip the
explicit tool call and proceed directly to `invoke_agent` or `mcp_invoke_tool`.

### `Bm25Ranker`

`Bm25Ranker` (in `RockBot.Host`) is now a `public static` class exposable to other projects.
It provides two overloads:

| Method | Returns |
|---|---|
| `Rank<T>(candidates, getDocumentText, query)` | `IReadOnlyList<T>` sorted by relevance (zero-score entries excluded) |
| `RankWithScores<T>(candidates, getDocumentText, query)` | `IReadOnlyList<(T Item, double Score)>` — same ordering but with raw BM25 scores for normalization |

Both use Okapi BM25 (k1=1.5, b=0.75) with a 2× phrase bonus for adjacent two-word query terms.

### DI registration

```csharp
agent.AddServiceSearch();   // registers search_known_services tool + IServiceSearchIndex
                             // + ServiceSearchSkillProvider (tool guide)
                             // + per-turn context hints in AgentContextBuilder
```

`AddServiceSearch()` must be called after `AddA2ACaller()` and `AddMcpToolProxy()` so
`IAgentDirectory` and `McpServerIndex` are already registered.

---

## Web tools (`RockBot.Tools.Web`)

Two tools — `web_search` and `web_browse` — give the agent access to the internet.

### `web_search(query, count?)`

Calls the Brave Search API and returns a numbered markdown list:

```
1. [Title](https://example.com) — Snippet text
2. ...
```

Configuration:
```csharp
opts.ApiKey = "...";           // or opts.ApiKeyEnvVar = "BRAVE_API_KEY"
opts.MaxSearchResults = 5;     // default
```

### `web_browse(url)`

Fetches a web page and converts it to markdown using AngleSharp (HTML parsing) and
ReverseMarkdown. Noise elements (scripts, styles, nav, footer, sidebars) are stripped before
conversion.

**Large page chunking:** When the markdown content exceeds `ChunkingThreshold` (default 8000
characters), the page is split into chunks using `ContentChunker` (from `RockBot.Host`):

1. Splits on H1/H2/H3 headings first (respects document structure)
2. Falls back to blank-line splitting for oversized sections
3. Hard-splits at `ChunkMaxLength` as a last resort
4. Stores each chunk in working memory under the session namespace: `session/{sessionId}/web-{sanitized-url}-chunk{n}`
5. Stores a hierarchical document outline as an index chunk at `session/{sessionId}/web-{sanitized-url}-index`
6. Returns a chunk index table listing heading and key for each chunk, plus the index chunk key

The agent can then call `get_from_working_memory` for specific chunks rather than loading the
full page into context at once.

**GitHub API routing:** `GitHubApiWebBrowseProvider` intercepts GitHub issue and PR URLs and
routes them through the GitHub REST API instead of the browser view:

- `github.com/{owner}/{repo}/issues/{number}` → `api.github.com/repos/{owner}/{repo}/issues/{number}`
- `github.com/{owner}/{repo}/pull/{number}` → `api.github.com/repos/{owner}/{repo}/pulls/{number}`

This returns cleaner structured data (title, state, author, labels, body) rather than HTML
rendered for humans. Public repos work without authentication.

### DI registration

```csharp
agent.AddWebTools(opts =>
{
    opts.ApiKey = config["WebTools:ApiKey"];
    opts.ChunkingThreshold = 8000;
    opts.ChunkMaxLength = 4000;
    opts.ChunkTtlMinutes = 30;
});
```

---

---

## Tool result chunking (all tools)

Any tool — MCP, REST, web, or built-in — can return a response large enough to overflow the
model's context window when the result is appended to the conversation history. The agent host
defends against this automatically in `UserMessageHandler`.

**How it works:**

After each tool call (both native function calls and text-based calls), the result string is
checked against a per-model threshold. If the result exceeds that threshold:

1. `ContentChunker` splits it into chunks (heading-aware, then blank-line, then hard-split)
2. Each chunk is stored in working memory under the session namespace: `{namespace}/tool-{name}-{runId}-chunk{n}`, TTL 20 minutes
3. A **hierarchical index chunk** is stored at `{namespace}/tool-{name}-{runId}-index` containing a document outline that maps section headings (with H1/H2/H3 nesting preserved) to chunk keys
4. A compact index table is returned to the LLM instead of the raw content, including the index chunk key:

```
Tool result for 'list_models' is large (462 000 chars) and has been split into 23 chunk(s)
stored in working memory.
A document outline is stored at key `session/abc123/tool-list_models-a1b2c3-index` — retrieve
it with get_from_working_memory to navigate the content by section heading.
Call get_from_working_memory(key) for each relevant chunk BEFORE drawing conclusions.

| # | Heading | Key                                              |
|---|---------|--------------------------------------------------|
| 0 | Part 0  | `session/abc123/tool-list_models-a1b2c3-chunk0`  |
| 1 | Part 1  | `session/abc123/tool-list_models-a1b2c3-chunk1`  |
...
```

The index chunk contains a hierarchical outline like:

```
## Document Outline

- **Models Overview** → `session/abc123/tool-list_models-a1b2c3-chunk0`
  - **Pricing** → `session/abc123/tool-list_models-a1b2c3-chunk1`
  - **Context Windows** → `session/abc123/tool-list_models-a1b2c3-chunk2`
    - **Token Limits** → `session/abc123/tool-list_models-a1b2c3-chunk3`
```

If the inline index is lost due to context trimming, the agent can retrieve the index chunk
from working memory to rediscover the document structure and navigate to specific sections.

Since chunk keys are full path strings (contain `/`), `get_from_working_memory` treats them as
absolute — no namespace prefix is prepended.

If working memory is unavailable (no session context), the result is truncated at the threshold
with a `[result truncated — N chars omitted]` notice — same fallback as `web_browse`.

**Per-model threshold configuration:**

The default threshold is **64 000 characters** (~16 000 tokens), appropriate for models with
120K+ token context windows. When chunking occurs, each chunk is sized up to the threshold
(minimum 20 000 chars) to minimise the number of working-memory retrievals.

Tune it per model in `appsettings.json`:

```json
{
  "ModelBehaviors": {
    "Models": {
      "openrouter/deepseek": {
        "ToolResultChunkingThreshold": 32000
      }
    }
  }
}
```

Lower the threshold for small-context models. Setting it very high effectively disables
proactive chunking while still relying on the reactive `TrimLargeToolResults` overflow
recovery as a safety net.

---

## Scheduling tools (`RockBot.Tools.Scheduling`)

Three tools for managing recurring and one-time scheduled tasks.

### Tools

| Tool | Purpose |
|---|---|
| `schedule_task(name, cron, description, run_once?)` | Create or replace a scheduled task |
| `list_scheduled_tasks()` | Markdown table of all tasks with next-fire times |
| `cancel_scheduled_task(name)` | Remove a task by name |

### Cron format

Both 5-field (minute precision) and 6-field (second precision) cron expressions are supported:

```
# 5-field: minute hour day-of-month month day-of-week
0 9 * * 1-5        # 9 AM every weekday
*/15 * * * *       # every 15 minutes

# 6-field: second minute hour day-of-month month day-of-week
0 0 9 * * 1-5      # 9:00:00 AM every weekday
```

**One-time tasks:** Set `run_once: true`. Pin all time fields to the exact target time; use `*`
for day-of-week. The scheduler automatically removes the task after it fires.

**Relative times:** The scheduler always shows current time and timezone in `list_scheduled_tasks`
output so the agent can compute correct cron expressions from requests like "remind me in 2 hours".

### DI registration

```csharp
agent.AddSchedulingTools();
```

---

## Script execution

Python scripts can be executed on-demand via `execute_python_script`. The execution model
differs between development and production.

### Tool interface

```
execute_python_script(
    script,           // Python source code (required)
    input_data?,      // Arbitrary string passed as ROCKBOT_INPUT env var
    timeout_seconds?, // Default 30s
    pip_packages?     // ["numpy", "requests"] — installed before execution
)
```

**Environment:** Python 3.12-slim. Only stdout is returned. The script should `print()` its
results (JSON recommended) and `exit(0)` on success.

**pip packages:** Installing packages adds startup latency (network + compilation). Cache
results in working memory or save as a skill to avoid re-installing on every call.

### Production: Kubernetes pods

`MessageBusScriptRunner` delegates to the Scripts Manager sidecar via RabbitMQ:

```
Agent: execute_python_script(...)
    │
    ▼
MessageBusScriptRunner
    │   Publishes ScriptInvokeRequest to "script.invoke"
    │   Awaits on "script.result.{agentName}"
    │
    ▼
Scripts Manager (trusted sidecar, separate pod)
    │   Has Kubernetes API access
    │   Creates ephemeral pod in "rockbot-scripts" namespace
    │   python:3.12-slim, 500m CPU, 256Mi RAM
    │   No network access, no persistent storage
    │   Runs script, streams stdout
    │   Deletes pod immediately after completion
    │
    ▼
ScriptInvokeResponse on "script.result.{agentName}"
```

The agent pod has **no Kubernetes API permissions** — it cannot create pods directly. All
script execution is delegated to the Scripts Manager, which has the minimal RBAC role needed
to create, watch, and delete pods in the `rockbot-scripts` namespace only.

### Development: local runner

`LocalScriptRunner` executes Python scripts directly on the local machine using the system
Python installation. No Kubernetes required.

```csharp
// Development
agent.AddLocalScriptRunner();

// Production
agent.AddRemoteScriptRunner(agentName: identity.Name);
```

### Security model

| Constraint | Kubernetes | Local |
|---|---|---|
| Network access | Denied (no network policy) | Unrestricted |
| Filesystem | Ephemeral pod only | Host filesystem |
| Credentials | None mounted | Inherits process env |
| Resource limits | 500m CPU, 256Mi RAM | Unrestricted |
| Cleanup | Pod deleted after completion | Process exits |

---

## OpenRouter MCP server (`McpServer.OpenRouter`)

An optional standalone MCP server that exposes read-only tools for querying OpenRouter account
information. Deployed as `rockbot-openrouter-mcp` when `openrouterMcp.enabled: true` in Helm
values.

### Tools

| Tool | Purpose |
|---|---|
| `get_credits` | Current account credit balance |
| `get_api_key_info` | Rate limits and usage for the active API key |
| `list_models` | Available models with context lengths and pricing |
| `list_api_keys` | Provisioned API keys (requires management key) |
| `get_api_key(keyHash)` | Details for a specific API key |
| `get_generation(generationId)` | Completion details including token counts and cost |

All tools return JSON strings from the OpenRouter REST API. No write operations are exposed —
this server cannot spend credits, create keys, or modify account settings.

### Configuration

```yaml
# In values.personal.yaml
openrouterMcp:
  enabled: true
secrets:
  openRouter:
    apiKey: "<your-openrouter-management-api-key>"
```

The agent connects to this server via `mcp.json` on the data volume.

---

## Diagnostics

`ToolDiagnostics` records zero-allocation metrics via `System.Diagnostics.Metrics`:

| Metric | Type | Tags |
|---|---|---|
| `rockbot.tool.invoke.duration` | Histogram (ms) | `tool_name`, `status` |
| `rockbot.tool.invocations` | Counter | `tool_name`, `status` |

Trace activities (kind `Internal`) are created per invocation and are automatically connected
to the parent distributed trace from the incoming message envelope.

---

## DI registration summary

```csharp
services.AddRockBotHost(agent =>
{
    // Core tool infrastructure (required for all tools)
    agent.AddToolHandler();             // IToolRegistry + ToolGuideTools + ToolInvokeHandler

    // Tool subsystems (add as needed)
    agent.AddMcpToolProxy();            // MCP management tools (message-bus proxy to bridge)
    // OR:
    agent.AddMcpTools(opts => ...);     // MCP bridge in-process (no message-bus hop)

    agent.AddWebTools(opts => ...);     // web_search + web_browse
    agent.AddSchedulingTools();         // schedule_task + list/cancel
    agent.AddSubagents();               // spawn_subagent + cancel/list + whiteboard
    agent.AddWisps(opts => ...);        // spawn_wisps (lightweight procedural pipelines, parallel batches)
    agent.AddRemoteScriptRunner();      // execute_python_script (Kubernetes)
    // OR:
    agent.AddLocalScriptRunner();       // execute_python_script (local dev)

    agent.AddA2ACaller(opts => ...);    // invoke_agent + list_known_agents + get_agent_details
    agent.AddServiceSearch();           // search_known_services (after AddA2ACaller + AddMcpToolProxy)
});
```
