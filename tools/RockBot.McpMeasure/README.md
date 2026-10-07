# RockBot.McpMeasure

This tool measures how well each model calls downstream MCP tools in each typed-tool mode (#613). It ports mcp-aggregator#42's method to RockBot's real stack:

- **The agent loop:** `AgentLoopRunner` over `RockBotFunctionInvokingChatClient`, or the text-based path when the model's behavior asks for it.
- **The gateway:** the agent-side MCP gateway plus the real `McpBridgeService`.
- **Fixture servers:** real MCP servers over streamable HTTP on loopback, all in one process. There is no RabbitMQ and no cluster.

## What it runs

**Modes** (`McpBridge:WrapperMode`):

| Mode | What the model gets |
|---|---|
| `Off` | `mcp_invoke_tool` only |
| `Eager` | every typed `{server}__{tool}` tool up front |
| `Lazy` | `mcp_find_tools` and per-session activation |
| `Pinned` | lazy, plus every tool of a server the session has called |

**Fixture servers** (`Fixtures/FixtureServers.cs`):

| Server | Mirrors |
|---|---|
| `adjutant` | calendar-mcp's 29 tools: their parameter names, required string, array and date arguments, and the "call list_accounts first" instructions |
| `onedrive-marimer`, `onedrive-personal` | each other: the tool names are identical |
| `todo` | the TodoApp tools |

**Tasks** (`Measurement/MeasureTasks.cs`):
- `calendar_tomorrow`
- `send_email`
- `list_files_marimer`: there is a look-alike server.
- `add_todo`
- `reply_thread`: two turns on adjutant. The second needs a tool the first didn't use, which is where `Pinned` differs from `Lazy`.

**Prompt.** The system prompt is the agent's own profile from `src/RockBot.Agent/agent/`: soul, safety rules, common directives, directives and memory rules, plus the model's `AdditionalSystemPrompt`. `--prompt minimal` uses mcp-aggregator#42's one-paragraph prompt instead.

**Loop settings.** Follow-up, completion evaluation and reasoning scaffolding are off, so a turn ends when the model answers.

## Metrics, per turn

| Metric | Meaning |
|---|---|
| **First call correct** | The first call that tried to reach a downstream tool went to the right server and tool, was valid for the tool's schema, and did what was asked (the task's check). |
| **First call well-formed** | That first call reached *some* existing tool with schema-valid arguments, so the gateway would deliver it. |
| **Completed** | The target fixture tool received a valid call at some point during the turn. The fixture servers record every call they get. |
| **Tool calls / downstream attempts** | All the model's calls, and the ones aimed at a downstream tool. Gateway calls such as `mcp_find_tools` count only in the first. |
| **Input / cached / output tokens** | Summed over the turn's LLM round trips. |
| **Wall time** | For the whole turn. |

When the first call isn't correct, the failure category records why:

| Category | Meaning |
|---|---|
| `empty_arguments` | `invoke_tool({})`, or required arguments missing and nothing sent |
| `invented_server` | the server doesn't exist |
| `typed_name_as_tool_name` | `tool_name` holds a `{server}__{tool}` name |
| `invented_tool` | the tool doesn't exist |
| `bare_tool_name` | the downstream tool was called by its bare name, as if it were in the list |
| `wrong_key` | a required key is missing while others were sent |
| `wrong_type` | a value has the wrong JSON type, or isn't a date |
| `wrong_server` | the right tool on the wrong server |
| `other_tool` | a valid call to a different tool, such as `list_accounts` first |
| `wrong_value` | valid, but not what was asked |
| `tool_call_as_text` | no call made, but the reply writes one out |
| `no_attempt` | no call made |
| `error` | the turn threw |

The grader accepts what the gateway accepts: nested `arguments`, `params` or `args`, or flattened keys, with a case-insensitive server name.

## Running it

```bash
# Smoke test, no key needed: the scripted model does the right thing on every surface.
dotnet run --project tools/RockBot.McpMeasure -- --scripted --runs 1

# What a real run would do, and the baseline context per mode, without calling a model.
dotnet run --project tools/RockBot.McpMeasure -- --dry-run

# The measurement.
dotnet run --project tools/RockBot.McpMeasure -- --runs 10 --out docs/measurements/raw/<date>
```

**Configuration.** Models and providers come from `measure.json` (`Measure:Models`, `Measure:Providers`, `Measure:DefaultModels`), then user-secrets, then the environment. Keys never go in the file:

```bash
dotnet user-secrets --project tools/RockBot.McpMeasure set Measure:Providers:azure:Endpoint https://<resource>.cognitiveservices.azure.com/openai/v1/
dotnet user-secrets --project tools/RockBot.McpMeasure set Measure:Providers:azure:ApiKey <key>
dotnet user-secrets --project tools/RockBot.McpMeasure set Measure:Providers:openrouter:ApiKey <key>
# or env: Measure__Providers__azure__ApiKey=...
```

**Options:**

| Option | Purpose |
|---|---|
| `--models high,low` | Pick models. |
| `--modes Eager,Lazy` | Pick modes. |
| `--tasks send_email` | Pick tasks. |
| `--max-iterations` | Cap the tool loop per turn. |
| `--verbose` | Print every call. |
| `--log-level Information` | Show RockBot's own logs. |

**Ordering.** Each run interleaves the modes, so drift in the provider during a session doesn't land on one mode.

**Output.** The tool writes `results.jsonl`, one row per turn, including every downstream attempt and its classification, and `summary.md` with the tables.
