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
    ├── Local executor (web, REST, scheduling, in-process MCP)
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
4. Stores each chunk in working memory: `web:{sanitized-url}:chunk:{n}`
5. Returns a chunk index table listing heading and key for each chunk

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
2. Each chunk is stored in working memory: `tool:{name}:{runId}:chunk{n}`, TTL 20 minutes
3. A compact index table is returned to the LLM instead of the raw content:

```
Tool result for 'list_models' is large (462 000 chars) and has been split into 23 chunk(s)
stored in working memory.
Call get_from_working_memory(key) for each relevant chunk BEFORE drawing conclusions.

| # | Heading | Key                           |
|---|---------|-------------------------------|
| 0 | Part 0  | `tool:list_models:a1b2c3:chunk0` |
| 1 | Part 1  | `tool:list_models:a1b2c3:chunk1` |
...
```

If working memory is unavailable (no session context), the result is truncated at the threshold
with a `[result truncated — N chars omitted]` notice — same fallback as `web_browse`.

**Per-model threshold configuration:**

The default threshold is **16 000 characters** (~4 000 tokens), suitable for most 32K–128K
context models. Tune it per model in `appsettings.json`:

```json
{
  "ModelBehaviors": {
    "Models": {
      "openrouter/google/gemini-2.0-flash": {
        "ToolResultChunkingThreshold": 64000
      },
      "openrouter/deepseek": {
        "ToolResultChunkingThreshold": 8000
      }
    }
  }
}
```

Raise the threshold for large-context models (1M tokens) or lower it for small-context models.
Setting it very high effectively disables proactive chunking while still relying on the reactive
`TrimLargeToolResults` overflow recovery as a safety net.

---

## REST tools (`RockBot.Tools.Rest`)

Exposes arbitrary HTTP endpoints as agent tools. Useful for internal APIs that don't have an
MCP server.

### Configuration

```json
{
  "RestTools": {
    "Endpoints": [
      {
        "Name": "get_weather",
        "Description": "Get current weather for a city",
        "UrlTemplate": "https://api.weather.com/v1/current?city={city}",
        "Method": "GET",
        "ParametersSchema": "{ \"type\": \"object\", \"properties\": { \"city\": { \"type\": \"string\" } } }",
        "AuthType": "api_key",
        "AuthEnvVar": "WEATHER_API_KEY",
        "ApiKeyHeader": "X-Api-Key"
      }
    ]
  }
}
```

`RestToolExecutor` performs URL template expansion (substituting `{param}` placeholders from
the JSON arguments), applies auth (`bearer` or `api_key` via header), and for POST/PUT/PATCH
can send remaining arguments as a JSON body.

### DI registration

```csharp
agent.AddRestTools(opts =>
    builder.Configuration.GetSection("RestTools").Bind(opts));
```

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
    agent.AddRestTools(opts => ...);    // REST endpoint tools
    agent.AddSchedulingTools();         // schedule_task + list/cancel
    agent.AddSubagents();               // spawn_subagent + cancel/list + whiteboard
    agent.AddRemoteScriptRunner();      // execute_python_script (Kubernetes)
    // OR:
    agent.AddLocalScriptRunner();       // execute_python_script (local dev)
});
```
