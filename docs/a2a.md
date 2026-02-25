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

## KEDA ephemeral pattern

`ResearchAgent` uses the ephemeral one-shot pattern:

- Deployed as a KEDA `ScaledJob` triggered by the `agent.task.ResearchAgent` queue.
- Spins up when a task arrives, completes it, then exits (`EphemeralShutdownService`).
- Registered in `well-known-agents.json` so the primary agent always knows it
  exists and can invoke it without waiting for a live announcement.
