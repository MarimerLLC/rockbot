# Agent-to-Agent (A2A) Communication

RockBot can invoke external agents over the RabbitMQ message bus using the
A2A protocol. The primary agent dispatches a task to a named agent, receives
streaming status updates while the external agent works, and gets a final
result folded back into the conversation.

---

## How it works

1. The primary agent calls `invoke_agent(agent_name, skill, message)`.
2. The request is published to `agent.task.{agentName}`.
3. The target agent processes the task, sending `Working` status updates.
4. On completion the target publishes a result to `agent.response.{callerName}`.
5. `A2ATaskResultHandler` stores the result in working memory at
   `session/{sessionId}/a2a/{agentName}/{taskId}/result` (60-minute TTL) and
   injects a synthetic user turn into the conversation that contains the exact
   key. The primary agent calls `get_from_working_memory` with that key to
   retrieve and present the result.

The external agent does **not** need to be running at the moment `invoke_agent`
is called — the message sits on the queue until the agent starts (e.g. a KEDA
ScaledJob spins up).

> **Result retrieval**: The result is always stored in working memory regardless
> of size. The synthetic turn that arrives in the conversation is a notification,
> not the result itself — the agent must call `get_from_working_memory` with the
> provided key to read the actual content before responding to the user.

---

## Agent discovery

Three tools provide different entry points into the agent directory:

| Tool | Use when |
|---|---|
| `search_known_services(query)` | You have a task and need to find which service (agent **or** MCP server) can handle it — single BM25 call covers both namespaces |
| `list_known_agents(skill?)` | You want to browse all known agents, optionally filtered by skill ID |
| `get_agent_details(agent_name)` | You need the full agent card (all skill fields, tags, examples, URL) for a specific agent |

`list_known_agents` returns agents currently in the local directory. The
directory is populated two ways:

### Auto-discovery (live announcements)

Agents that call `AddA2A()` broadcast their `AgentCard` on the
`discovery.announce` topic at startup and every 2 minutes. The primary agent
receives these and stores them in `AgentDirectory` (persisted to
`known-agents.json` on the PVC).

This works well for long-running agents. Ephemeral agents (e.g. KEDA
ScaledJobs) are **not** running between invocations, so they will not appear
in the directory between runs.

### Well-known agents (static config file)

For agents that cannot reliably announce themselves — ephemeral/KEDA agents,
agents on a different restart schedule, or any agent you want to guarantee is
always listed — add them to the **`well-known-agents.json`** file on the agent
PVC (`/data/agent/well-known-agents.json`).

```json
[
  {
    "agentName": "ResearchAgent",
    "description": "On-demand research agent. Searches the web, fetches pages, and synthesises answers using an LLM.",
    "version": "1.0",
    "skills": [
      {
        "id": "research",
        "name": "Research",
        "description": "Research a topic using web search and page fetching, then synthesise a concise answer."
      }
    ]
  }
]
```

Well-known agents:
- Always appear in `list_known_agents` regardless of whether they are running.
- Show `lastSeen: "well-known (not yet seen this session)"` until they announce
  themselves, after which the real timestamp is shown.
- Are **never removed** from the directory by a deregistration announcement
  (e.g. a KEDA pod shutting down after completing its task).
- Can be invoked with `invoke_agent` at any time — the message waits on the
  queue until the agent pod starts.

> **Rule of thumb**: Any agent that is not a permanently-running deployment
> should be listed in `well-known-agents.json`. This includes KEDA ScaledJobs,
> agents that restart frequently, and any agent whose startup timing relative
> to the primary agent is unpredictable.

---

## Implementing an A2A agent

### Queue-based (RabbitMQ) agent

Call `AddA2A()` in the agent's `Program.cs` and register an `IAgentTaskHandler`:

```csharp
agent.AddA2A(opts =>
{
    opts.Card = new AgentCard
    {
        AgentName = "MyAgent",
        Description = "What this agent does.",
        Version = "1.0",
        Skills = [new AgentSkill { Id = "my-skill", Name = "My Skill", Description = "..." }]
    };
});

agent.Services.AddScoped<IAgentTaskHandler, MyAgentTaskHandler>();
```

The handler receives an `AgentTaskRequest`, can publish `AgentTaskStatusUpdate`
messages (state `Working`) while processing, and must publish either an
`AgentTaskResult` or `AgentTaskError` when done.

See `RockBot.ResearchAgent` and `RockBot.SampleAgent` for working examples.

---

### HTTP-based agent

For agents that communicate over HTTP rather than queued messaging, use
`RockBot.SampleAgent.Http` as the reference implementation.

An HTTP agent is a standard ASP.NET Core `WebApplication` that exposes two endpoints:

| Endpoint | Description |
|---|---|
| `GET /.well-known/agent.json` | Returns the `AgentCard` describing the agent |
| `POST /tasks/send` | Accepts an `AgentTaskRequest`, processes it, returns `AgentTaskResult` |

Unlike queue-based agents, an HTTP agent returns the result **synchronously** in the
HTTP response body. There is no reply-to queue; the caller waits for the response.

HTTP agents stay in memory listening for inbound calls rather than the KEDA
on-demand pattern. They are suitable for low-latency use cases or environments
where a message broker is not available.

#### Registration with the primary agent

Because HTTP agents may not be connected to the RabbitMQ bus, they cannot
auto-announce themselves via the discovery topic. Register them in
`well-known-agents.json` on the agent PVC and include the agent's base URL:

```json
[
  {
    "agentName": "SampleAgent-Http",
    "description": "Sample HTTP agent.",
    "version": "1.0",
    "url": "http://sampleagent-http:5100",
    "skills": [
      { "id": "echo", "name": "Echo", "description": "Echoes the input back." },
      { "id": "general", "name": "General Task", "description": "General-purpose LLM task." }
    ]
  }
]
```

When `invoke_agent` is called for an agent whose `AgentCard` has a non-empty `Url`,
the primary agent dispatches the task over HTTP to `{Url}/tasks/send` instead of
publishing to the message bus. The result is folded into the conversation by the
same `A2ATaskResultHandler` used for queue-based results.

See `RockBot.SampleAgent.Http` for a complete working example.

---

## A2A HTTP Gateway (inbound)

`RockBot.A2A.Gateway` is an ASP.NET Core HTTP gateway that accepts inbound A2A v1
JSON-RPC requests from external clients and bridges them to the RockBot agent over
RabbitMQ. This is the reverse of `invoke_agent` — instead of RockBot calling out,
external agents call **in**.

### Endpoints

| Endpoint | Auth | Description |
|---|---|---|
| `GET /.well-known/agent-card.json` | None | A2A discovery — returns the agent card with capabilities and security schemes |
| `POST /` | Required | JSON-RPC 2.0 dispatch for all A2A methods |

### Supported JSON-RPC methods

| Method | Response | Description |
|---|---|---|
| `SendMessage` / `message/send` | JSON | Send a message and wait for the agent's response |
| `SendStreamingMessage` / `message/sendStream` | SSE | Send a message and stream status updates + final response as Server-Sent Events |
| `GetTask` | JSON | Retrieve the current state of a task by ID |
| `ListTasks` | JSON | List tasks with optional status filter and pagination |
| `CancelTask` | JSON | Request cancellation of an in-flight task |
| `SubscribeToTask` | SSE | Attach to an existing task and receive future events as SSE |
| `CreateTaskPushNotificationConfig` | JSON | Register a webhook URL to receive task status changes |
| `GetTaskPushNotificationConfig` | JSON | Get a push notification config by ID |
| `ListTaskPushNotificationConfig` | JSON | List push configs for a task |
| `DeleteTaskPushNotificationConfig` | JSON | Remove a push notification config |
| `GetExtendedAgentCard` | JSON | Return the agent card with full capabilities (authenticated) |

### Authentication

The gateway currently supports **X-Api-Key** header authentication. Each API key maps
to an agent identity (agent ID + display name) configured in the `ApiKeys` section of
`appsettings.json`. JWT/Bearer authentication is planned (#264).

### SSE streaming

Streaming methods (`SendStreamingMessage`, `SubscribeToTask`) return
`Content-Type: text/event-stream`. Each SSE event is a JSON-RPC 2.0 result wrapping a
`StreamResponse` (which contains either a `TaskStatusUpdateEvent`, `TaskArtifactUpdateEvent`,
or final `Message`):

```
data: {"jsonrpc":"2.0","id":1,"result":{"statusUpdate":{"taskId":"abc","status":{"state":"working"}}}}

data: {"jsonrpc":"2.0","id":1,"result":{"message":{"role":"agent","parts":[{"text":"Done."}]}}}

```

Under the hood, the gateway subscribes to the RabbitMQ `agent.task.status` topic and
the per-caller reply topic, forwarding events to the SSE stream as they arrive.

### Task persistence

Tasks are stored in a file-backed task store (`tasks.json` on the PVC) so they survive
pod restarts. `ListTasks` supports filtering by status, context ID, timestamp, and
cursor-based pagination. Tasks are scoped per authenticated caller.

### Push notifications

When a push notification config is registered for a task, the gateway sends an HTTP
POST to the configured webhook URL on every task status change. The webhook body is
the same `StreamResponse` JSON used in SSE streaming. Configs are persisted to
`push-configs.json` on the PVC.

### Example request

```bash
curl -X POST http://localhost:5200/ \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: my-key" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "SendMessage",
    "params": {
      "message": { "role": "user", "parts": [{ "text": "What is the weather?" }] },
      "metadata": { "skill": "notify-user" }
    }
  }'
```

---

## KEDA ephemeral pattern

`ResearchAgent` uses the ephemeral one-shot pattern:

- Deployed as a KEDA `ScaledJob` triggered by the `agent.task.ResearchAgent` queue.
- Spins up when a task arrives, completes it, then exits (`EphemeralShutdownService`).
- Registered in `well-known-agents.json` so the primary agent always knows it
  exists and can invoke it without waiting for a live announcement.
