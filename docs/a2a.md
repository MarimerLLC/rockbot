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
4. If the response is non-terminal (Working/Submitted), the caller polls or
   waits for follow-up messages until a terminal state is reached.
5. If the response is `InputRequired`, the caller generates a trust-gated
   follow-up response and sends it back to continue the multi-turn conversation.
6. On completion the target publishes a result to `agent.response.{callerName}`.
7. `A2ATaskResultHandler` stores the result in working memory at
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

## Long-running tasks and multi-turn interaction

RockBot supports the full A2A task lifecycle — not just single-shot request/response.
This enables scenarios like two RockBot instances collaborating on behalf of their
users (e.g., negotiating a meeting time).

### Long-running task polling (HTTP transport)

When an HTTP-transport agent returns a non-terminal state (`Working` or `Submitted`),
the outbound dispatcher polls `GetTask` with exponential backoff until the task
reaches a terminal state (`Completed`, `Failed`, `Canceled`) or transitions to
`InputRequired`.

| Setting | Default | Description |
|---|---|---|
| `PollingInitialDelay` | 2s | First polling delay |
| `PollingMaxDelay` | 30s | Maximum delay (backoff cap) |

Intermediate `Working` status updates are forwarded to the user via the internal
message bus so they can see progress in real time.

### InputRequired multi-turn follow-up

When a remote agent returns `state: inputRequired`, the caller automatically
generates a follow-up response and sends it back with the same `contextId` to
continue the conversation. This works on **both** HTTP and queue transports.

**Trust-gated behavior**: The response generation is gated by the existing
[trust model](#trust-levels):

- If the target agent has **Act-level trust** and the skill is in `ApprovedSkills`:
  the LLM generates the follow-up response autonomously using its full tool set
  (calendar access, memory search, etc.).
- Otherwise: the question is surfaced through the user's conversation. The LLM
  still generates a response, but the framing asks it to involve the user.

Both paths run through `AgentLoopRunner` with the complete tool set, so the LLM
can look up calendars, check working memory, invoke skills, etc.

**Transport differences**:

| Transport | Mechanism |
|---|---|
| HTTP | The dispatch loop calls `SendMessage` again with the same `contextId` |
| Queue (RabbitMQ) | `A2ATaskResultHandler` publishes a follow-up `AgentTaskRequest` with the same `contextId` and `correlationId` |

### Loop protection

Two safety mechanisms prevent infinite InputRequired ping-pong:

1. **Max round limit** (`MaxInputRequiredRounds`, default **20**): Hard cap on
   the number of follow-up round-trips. When exceeded, the task is terminated
   with a descriptive error.

2. **Repetition detection** (`InputRequiredRepetitionThreshold`, default **3**):
   Detects when the same question/answer pair repeats consecutively. When
   triggered, the loop is broken with a suggestion to restructure the request.

### ContextId and conversation continuity

The `contextId` field on `AgentTaskRequest` links messages that belong to the same
multi-turn conversation. On the **inbound** side, `RockBotTaskHandler` uses the
`contextId` to derive the LLM session ID (`a2a-inbound/{contextId}`). When a
follow-up message arrives with a known `contextId`, the handler rebuilds the chat
context from stored conversation history so the LLM sees the full exchange.

### Observability

All polling and InputRequired activity is instrumented with OpenTelemetry:

- **Metrics**: `rockbot.a2a.polling_attempts`, `rockbot.a2a.input_required_rounds`,
  `rockbot.a2a.input_required_breaks`
- **Spans**: `rockbot.a2a.poll_loop`, `rockbot.a2a.input_required_loop`,
  `rockbot.a2a.input_required_round`
- **Cross-container tags**: Every span includes `task_id`, `context_id`,
  `correlation_id`, and `session_id` so distributed traces can be stitched
  across caller and responder containers.

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

## Implementing inbound A2A skills

RockBot dispatches inbound A2A requests through `RockBotTaskHandler` based on
trust level and skill ID. To add a new Act-level skill, follow this pattern.

### 1. Register the skill

Add it in three places:

1. **Agent card** in `Program.cs` — so other agents can discover it:
   ```csharp
   new AgentSkill { Id = "my-skill", Name = "My Skill",
       Description = "What this skill does" }
   ```

2. **Gateway appsettings.json** — so HTTP callers see it in the agent card:
   ```json
   { "Id": "my-skill", "Name": "My Skill", "Description": "..." }
   ```

3. **Skill dispatch** in `RockBotTaskHandler.HandleTaskAsync` — route to your handler:
   ```csharp
   "my-skill" => await HandleMySkillAsync(request, identity, context, ct),
   ```

### 2. Persist outcomes in working memory

Every skill that produces a meaningful result **must** store it in working memory
so the agent can recall it later. Without this, the agent has no memory of the
interaction after the request completes.

```csharp
await workingMemory.SetAsync(
    $"a2a-outcomes/{request.Skill}/{contextId}",  // stable key pattern
    outcomeText,                                    // human-readable summary
    ttl: TimeSpan.FromHours(8),                     // long enough to be useful
    category: "a2a-outcome",                        // standard category for all A2A outcomes
    tags: [caller.DisplayName, "meeting"]);          // searchable tags
```

**Requirements:**
- **Key pattern**: `a2a-outcomes/{skillId}/{contextId}` — consistent across all skills
- **Category**: always `"a2a-outcome"` — enables `SearchWorkingMemory` by category
- **TTL**: 8 hours minimum — shorter TTLs risk the agent forgetting before the
  user asks about it
- **Tags**: include the caller's display name and domain-relevant keywords
- **Content**: include a timestamp, the caller identity, and enough context to
  be useful standalone (the agent may retrieve this hours later without the
  original conversation in context)

### 3. Multi-turn skills (InputRequired)

Skills that need follow-up information from the caller return
`AgentTaskState.InputRequired` instead of `Completed`. The caller's
`InputRequiredHandler` generates a response (trust-gated) and sends it back
with the same `contextId`.

**Required patterns for multi-turn skills:**

- **Use `contextId`** for session continuity:
  ```csharp
  var contextId = request.ContextId ?? request.TaskId;
  var sessionId = $"a2a-inbound/{contextId}";
  ```

- **Check conversation history** to distinguish first call from follow-up:
  ```csharp
  var existingTurns = await conversationMemory.GetTurnsAsync(sessionId, ct);
  var isContinuation = existingTurns.Count > 0;
  ```

- **Store turns** in conversation memory for future continuation:
  ```csharp
  await conversationMemory.AddTurnAsync(sessionId,
      new ConversationTurn("user", message, DateTimeOffset.UtcNow)
      { AgentName = caller.DisplayName }, ct);
  ```

- **Persist the outcome** when the multi-turn exchange completes (not on
  intermediate InputRequired rounds — only on the final Completed response).

See `HandleNegotiateMeetingAsync` in `RockBotTaskHandler.cs` for the reference
implementation.

### 4. Trust and approval

Callers must have Act-level trust (`AgentTrustLevel.Act`) **and** the skill ID
in their `ApprovedSkills` list to reach the Act-level dispatch. Otherwise the
request falls through to the Observe path (read-only LLM summary + user
notification).

Trust entries are managed in `agent-trust.json` on the data volume. For
docker-compose deployments, pre-seed the trust store with the skills you want
approved. For production, trust is granted incrementally by the user.

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
