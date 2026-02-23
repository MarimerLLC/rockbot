# Agent host

The agent host is the runtime that wires together messaging, LLM calls, memory, skills, tools,
and the dream cycle into a working agent process. It lives in `RockBot.Host` and
`RockBot.Host.Abstractions`, with the concrete `RockBot.Agent` project providing the runnable
executable.

---

## Overview

```
Incoming MessageEnvelope (from RabbitMQ)
    │
    ▼
IMessagePipeline.DispatchAsync()
    │
    ├── Middleware chain (logging, tracing, error handling, ...)
    │
    ▼
IMessageHandler<TMessage>.HandleAsync()
    │   UserMessageHandler — main LLM conversation loop
    │   ScheduledTaskHandler — scheduled task delivery
    │   ConversationHistoryRequestHandler — history replay
    │
    ├── IConversationMemory — sliding window of turns
    ├── ILongTermMemory — BM25 recall of relevant memories
    ├── ISkillStore — BM25 recall of relevant skills
    ├── IWorkingMemory — session-scoped scratch space
    ├── ILlmClient — serialized LLM gateway (one in-flight at a time)
    └── IFeedbackStore — quality signal writes (fire-and-forget)
```

---

## Agent identity and profile

### `AgentIdentity`

```csharp
public sealed record AgentIdentity(
    string Name,          // Logical agent name, e.g. "rockbot"
    string InstanceId     // Unique instance; auto-generated GUID if not supplied
);
```

Used in system prompt construction, topic subscriptions, and as the `Source` field on outgoing
envelopes.

### `AgentProfile`

The agent's personality and instructions are loaded from markdown files on the data volume:

| File | Purpose |
|---|---|
| `soul.md` | Core identity, values, and personality — stable; authored by prompt engineers |
| `directives.md` | Deployment-specific operational instructions |
| `style.md` | *(optional)* Voice and tone polish |
| `memory-rules.md` | *(optional)* Rules governing when and how memories are formed |

The profile is parsed into an `AgentProfile` composed of `AgentProfileDocument` instances. Each
document is split on `##` headings into named `AgentProfileSection` items. Sections can be
looked up by name across all documents via `profile.FindSection("name")`.

### `DefaultSystemPromptBuilder`

Assembles the system prompt from the agent profile and identity:

```
You are {AgentName}.

{soul.md content}

{directives.md content}

{memory-rules.md content}   ← if present

{style.md content}          ← if present
```

The result is cached after the first call — the profile is immutable at runtime. The built
system prompt is the starting system message on every LLM request.

---

## Message pipeline

### Registration

```csharp
agent
    .HandleMessage<UserMessage, UserMessageHandler>()
    .HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>()
    .HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>()
    .UseMiddleware<LoggingMiddleware>()
    .UseMiddleware<TracingMiddleware>()
    .UseMiddleware<ErrorHandlingMiddleware>()
    .SubscribeTo(UserProxyTopics.UserMessage)
    .SubscribeTo(UserProxyTopics.ConversationHistoryRequest);
```

### Dispatch flow

`IMessagePipeline` receives a raw `MessageEnvelope` from the subscriber callback:

1. Deserializes the `MessageType` field to find the registered `IMessageHandler<T>`
2. Passes the envelope through the middleware chain
3. Middleware calls `next()` to continue; or short-circuits by returning a `MessageResult`
4. The innermost middleware invokes the handler

`MessageTypeResolver` maps `MessageType` strings to .NET types. Registration is done via
`agent.HandleMessage<TMessage, THandler>()` which records both the type mapping and the DI
registration for `THandler`.

---

## Conversation memory

### `FileConversationMemory` (implements `IConversationMemory`)

Wraps `InMemoryConversationMemory` with file-backed persistence:

- Each session serializes to `{BasePath}/{sessionId}.json`
- On startup, sessions whose last turn falls within `SessionIdleTimeout` are reloaded — so
  recent conversations survive agent restarts
- Per-session `SemaphoreSlim` prevents concurrent write races on the same file
- If `IConversationLog` is registered, every turn is also appended to the conversation log for
  the dream preference-inference pass

**Session lifecycle:**
1. First message in a session creates the file
2. Subsequent messages append turns and re-serialize
3. `ClearAsync` removes both the in-memory state and the file
4. Stale sessions (beyond `SessionIdleTimeout`) are not loaded on restart

---

## Feedback and session evaluation

### `FileFeedbackStore` (implements `IFeedbackStore`)

Appends `FeedbackEntry` records to per-session JSONL files:

```
{BasePath}/{sessionId}.jsonl
```

One JSON object per line. Per-session semaphores prevent concurrent write races.

`QueryRecentAsync` scans all JSONL files to find entries since a given timestamp — used by the
dream cycle to gather quality signals for memory consolidation and skill optimization.

### `SessionSummaryService`

Background hosted service that evaluates completed sessions:

1. Polls on `FeedbackOptions.PollInterval` (default 5 minutes)
2. Finds sessions whose last turn is older than `SessionIdleThreshold` (default 10 minutes)
   that haven't already been evaluated this run
3. Backs off if the LLM is busy (polls every 5s until idle)
4. Sends the full session transcript to the LLM with an evaluator directive
5. Writes a `FeedbackEntry` with `SignalType = SessionSummary` containing:
   - `summary`: one-sentence description
   - `toolsWorkedWell`, `toolsFailedOrMissed`, `correctionsMade`
   - `overallQuality`: `excellent` / `good` / `fair` / `poor`

The evaluator directive is loaded from `session-evaluator.md` on the data volume, with a
built-in fallback.

The dream cycle's skill optimization pass uses `poor` / `fair` quality scores, along with
explicit `Correction` signals, to identify skills that need improvement.

---

## Conversation log

### `FileConversationLog` (implements `IConversationLog`)

Single-file JSONL log of all conversation turns across all sessions:

```
{BasePath}/turns.jsonl
```

A single semaphore serializes all writes. Used exclusively by the dream cycle:
- The preference-inference pass reads the full log to infer durable user preferences
- The skill gap detection pass reads it to find recurring patterns
- Both passes clear the log after processing to prevent unbounded growth

`IConversationLog` is **opt-in** — call `WithConversationLog()` explicitly in the host
builder. `WithMemory()` does not register it.

---

## LLM client

### `ILlmClient`

```csharp
public interface ILlmClient
{
    bool IsIdle { get; }
    Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);
}
```

A serialized gateway around the underlying `IChatClient` from `Microsoft.Extensions.AI`.
Enforces that only one LLM call is in flight at a time within the agent process:

- If a second call arrives while the first is running, it queues and waits
- `IsIdle` lets background services (dream cycle, session evaluator) back off while the user
  is waiting for a response

This prevents rate-limit collisions and ensures the agent never makes concurrent LLM calls that
could interleave tool results or corrupt conversation state.

---

## Per-model behaviors

Model-specific behavioral overrides are loaded from `model-behaviors/{model-prefix}/` on the
data volume. The model prefix is matched case-insensitively against the deployed model ID.

| File | Applied at |
|---|---|
| `additional-system-prompt.md` | Appended to every system prompt (guardrails, output constraints) |
| `pre-tool-loop-prompt.md` | Injected before each tool-calling iteration |

Additional properties are configurable in `appsettings.json` under `ModelBehaviors:Models:{prefix}`:

| Property | Type | Default | Purpose |
|---|---|---|---|
| `NudgeOnHallucinatedToolCalls` | bool | false | Inject a nudge when the model describes tool actions without emitting calls |
| `MaxToolIterationsOverride` | int? | null (uses 12) | Override the per-request tool-loop iteration cap |
| `ToolResultChunkingThreshold` | int? | null (uses 16 000) | Char count above which tool results are chunked into working memory instead of appended inline |
| `ScheduledTaskResultMode` | enum | `Summarize` | How scheduled task output is presented (`Summarize`, `VerbatimOutput`, `SummarizeWithOutput`) |

Example — raising the chunking threshold for a large-context model:

```json
{
  "ModelBehaviors": {
    "Models": {
      "openrouter/google/gemini-2.0-flash": {
        "ToolResultChunkingThreshold": 64000
      }
    }
  }
}
```

See [Tool result chunking](tools.md#tool-result-chunking-all-tools) for full details.

---

## Agent host builder

`AgentHostBuilder` is the fluent configuration API. Access it via `AddRockBotHost`:

```csharp
services.AddRockBotHost(agent =>
{
    agent.WithIdentity("rockbot");
    agent.WithProfile();                 // Load soul.md, directives.md, etc. from data volume
    agent.WithRules();                   // Load agent rules from rules/ directory
    agent.WithMemory();                  // Conversation + long-term + working memory
    agent.WithConversationLog();         // Opt-in: enables dream gap detection + pref inference
    agent.WithFeedback();                // IFeedbackStore + SessionSummaryService
    agent.WithSkills();                  // ISkillStore + ISkillUsageStore + StarterSkillService
    agent.WithDreaming(opts =>
    {
        opts.InitialDelay = TimeSpan.FromMinutes(5);
        opts.Interval = TimeSpan.FromHours(4);
    });

    // Message handlers
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>();
    agent.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();

    // Tool subsystems
    agent.AddToolHandler();              // Tool invocation dispatch
    agent.AddMcpToolProxy();             // MCP server bridge
    agent.AddWebTools(opts => { ... });  // Web search + browse
    agent.AddSchedulingTools();          // Scheduled task tools
    agent.AddRemoteScriptRunner();       // Script execution via Scripts Manager

    // Subscriptions
    agent.SubscribeTo(UserProxyTopics.UserMessage);
    agent.SubscribeTo(UserProxyTopics.ConversationHistoryRequest);

    // Optional middleware
    agent.UseMiddleware<LoggingMiddleware>();
    agent.UseMiddleware<TracingMiddleware>();
    agent.UseMiddleware<ErrorHandlingMiddleware>();
});
```

### Extension method reference

| Method | Registers |
|---|---|
| `WithIdentity(name)` | `AgentIdentity` |
| `WithProfile()` | `IAgentProfileProvider`, `AgentProfile`, `ISystemPromptBuilder` |
| `WithRules()` | `IRulesStore`, rules tools |
| `WithConversationMemory()` | `IConversationMemory` (file-backed + in-memory) |
| `WithLongTermMemory()` | `ILongTermMemory` (FileMemoryStore) |
| `WithWorkingMemory()` | `IWorkingMemory` (HybridCacheWorkingMemory) |
| `WithMemory()` | All three memory tiers above |
| `WithConversationLog()` | `IConversationLog` (FileConversationLog) |
| `WithFeedback()` | `IFeedbackStore` + `SessionSummaryService` |
| `WithSkills()` | `ISkillStore` + `ISkillUsageStore` + `StarterSkillService` |
| `WithDreaming()` | `DreamService` (IHostedService) |

---

## Agent data volume layout

All persistent agent state lives under a single base path (default `/data/agent` in production,
configurable via `AgentProfileOptions.BasePath`):

```
/data/agent/
├── soul.md                    # Core identity and personality
├── directives.md              # Operational instructions
├── style.md                   # (optional) Voice and tone
├── memory-rules.md            # (optional) Memory formation rules
├── dream.md                   # Dream: memory consolidation prompt
├── skill-dream.md             # Dream: skill consolidation prompt
├── skill-optimize.md          # Dream: skill optimization prompt
├── skill-gap.md               # Dream: skill gap detection prompt
├── pref-dream.md              # Dream: preference inference prompt
├── session-evaluator.md       # Session quality evaluation prompt
├── mcp.json                   # MCP server connection configuration
├── rules/                     # Agent rules (markdown files)
├── model-behaviors/           # Per-model prompt overrides
│   └── {model-prefix}/
│       ├── additional-system-prompt.md
│       └── pre-tool-loop-prompt.md
├── memory/                    # Long-term memory entries
│   └── {category}/
│       └── {id}.json
├── skills/                    # Learned skills
│   └── {name}.json            # (may be nested: skills/mcp/email.json)
├── skill-usage/               # Skill invocation event log
│   └── {sessionId}.jsonl
├── feedback/                  # Session quality signals
│   └── {sessionId}.jsonl
├── conversations/             # Persisted conversation sessions
│   └── {sessionId}.json
└── conversation-log/          # Aggregated turns for dream passes
    └── turns.jsonl
```

---

## Startup sequence

When the agent process starts:

1. `AgentHostBuilder.Build()` registers all services with the DI container
2. `IHostedService` implementations start in registration order:
   - `StarterSkillService` — seeds starter skills from registered `IToolSkillProvider`s
   - `McpBridgeService` — connects to configured MCP servers
   - `FileConversationMemory` — reloads sessions within `SessionIdleTimeout`
   - `SessionSummaryService` — begins polling for sessions to evaluate
   - `DreamService` — schedules first dream cycle after `InitialDelay`
   - `AgentHostService` — subscribes to configured topics and begins processing messages
3. The agent is now ready to receive messages

---

## Configuration reference

Key configuration sections (from `appsettings.json` or environment variables):

```json
{
  "AgentProfile": {
    "BasePath": "/data/agent"
  },
  "RabbitMq": {
    "HostName": "rabbitmq.cluster.local",
    "Port": 5672,
    "UserName": "rockbot",
    "Password": "..."
  },
  "AzureAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "Key": "...",
    "DeploymentName": "gpt-4o"
  },
  "Memory": {
    "BasePath": "memory"
  },
  "Skills": {
    "BasePath": "skills",
    "UsageBasePath": "skill-usage"
  },
  "Dream": {
    "Enabled": true,
    "InitialDelay": "00:05:00",
    "Interval": "04:00:00"
  },
  "Feedback": {
    "BasePath": "feedback",
    "SessionIdleThreshold": "00:10:00",
    "PollInterval": "00:05:00"
  }
}
```
