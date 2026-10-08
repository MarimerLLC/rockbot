# Messaging Design

## Overview

The messaging layer is RockBot's nervous system. Every interaction — agent-to-agent communication, LLM requests, tool invocations, script execution — flows through the message bus. This keeps components decoupled and independently deployable.

## Design Decisions

### Why Topic-Based Pub/Sub?

We considered three messaging patterns:

1. **Point-to-point queues**: Simple, but creates tight coupling between sender and receiver. Adding a new consumer means changing the sender.
2. **Fan-out pub/sub**: Every subscriber gets every message. No filtering, wasteful for targeted communication.
3. **Topic-based pub/sub**: Publishers send to topics, subscribers filter by topic patterns. Best of both worlds.

Topic-based pub/sub was chosen because:
- Agents can subscribe to broad patterns (`agent.*`) or specific topics (`agent.task.summarize`).
- New consumers can be added without modifying publishers.
- The same infrastructure supports both broadcast (logging, monitoring) and targeted (task assignment) patterns.

### Why Byte Arrays for the Body?

The `MessageEnvelope.Body` is `ReadOnlyMemory<byte>` rather than a typed object or string because:

- **Transport agnosticism**: Different providers serialize differently. Keeping the body as bytes means the envelope doesn't care.
- **Flexibility**: Payloads might be JSON, Protocol Buffers, or raw binary (e.g., script output). The envelope doesn't impose a format.
- **Performance**: Avoids double serialization (object → JSON → bytes → JSON → object) when the transport already handles serialization.

Convenience extension methods (`ToEnvelope<T>()` and `GetPayload<T>()`) provide JSON serialization for the common case.

### Why Manual Acknowledgment?

The `MessageResult` enum (Ack, Retry, DeadLetter) gives handlers explicit control over message lifecycle:

- **Ack**: Message processed successfully. Remove from queue.
- **Retry**: Transient failure (network timeout, rate limit). Requeue for another attempt.
- **DeadLetter**: Permanent failure (invalid message, unrecoverable error). Route to DLQ for human inspection.

This is critical for reliability. Auto-ack means messages are lost on handler failure. With manual ack, messages survive crashes and get redelivered.

Note: For messages that are acked early and processed in a background loop (e.g. `UserMessageHandler`), RabbitMQ's ack semantics alone cannot protect against mid-processing crashes. The [WIP tracking system](wip-tracking.md) addresses this by persisting the envelope to disk before dispatch and replaying incomplete entries on startup.

### Why Separate Publisher and Subscriber Interfaces?

Rather than a single `IMessageBus` interface, we split into `IMessagePublisher` and `IMessageSubscriber` because:

- **Interface segregation**: Most components only need one capability. An LLM handler publishes results but doesn't subscribe to anything directly (the host subscribes on its behalf).
- **Testability**: Easy to mock one without the other.
- **Implementation flexibility**: A publisher might use a different channel strategy than a subscriber.

## Message Envelope

```
┌──────────────────────────────────────────┐
│ MessageEnvelope                          │
├──────────────────────────────────────────┤
│ MessageId      : string (GUID)          │
│ MessageType    : string (CLR type name) │
│ CorrelationId  : string? (thread msgs)  │
│ ReplyTo        : string? (response topic)│
│ Source         : string (sender agent)  │
│ Destination    : string? (target agent) │
│ Timestamp      : DateTimeOffset (UTC)   │
│ Headers        : Dictionary<str,str>    │
│ Body           : ReadOnlyMemory<byte>   │
└──────────────────────────────────────────┘
```

### Field Usage Patterns

**CorrelationId** ties related messages together. When an agent sends a task to another agent, it generates a correlation ID. All messages related to that task (requests, responses, tool calls, results) carry the same correlation ID. This enables:
- Conversation threading
- Distributed tracing
- State lookup (correlation ID → conversation context)

**ReplyTo** tells the receiver where to send responses. This decouples the response routing from the sender's identity. An agent might want responses sent to a specific topic rather than its general inbox.

**Source** and **Destination** identify the sending and intended receiving agents. Destination is optional because some messages are broadcasts (e.g., status updates).

**MessageType** carries the CLR type name of the serialized payload. This enables handlers to deserialize without prior knowledge of the message contents, and supports polymorphic dispatch.

**Headers** carry extension metadata without changing the envelope schema. Planned uses:
- `priority`: Message priority for queue ordering
- `ttl`: Time-to-live for expiring messages
- `retry-count`: Number of delivery attempts
- `trace-id`: OpenTelemetry trace context

## Topic Naming Convention

Topics follow a hierarchical dot-separated naming scheme:

```
{domain}.{action}.{detail}
```

### Planned Topics

| Topic Pattern | Description |
|---|---|
| `agent.task.*` | Task assignment to agents |
| `agent.response.*` | Agent responses to tasks |
| `agent.status.*` | Agent lifecycle events (started, stopped, heartbeat) |
| `tool.invoke.*` | Tool/MCP invocation requests |
| `tool.invoke.mcp` | MCP-specific tool invocation requests (handled by bridge) |
| `tool.result.*` | Tool execution results |
| `tool.result.{agentName}` | Tool results routed to a specific agent |
| `tool.meta.mcp.*` | MCP tool discovery/availability messages |
| `tool.meta.mcp.{agentName}` | MCP tool availability for a specific agent |
| `tool.meta.mcp.refresh` | Request MCP bridge to re-discover tools |
| `script.invoke` | Script execution requests |
| `script.result` | Script execution results |
| `system.error` | System-level error events |
| `system.metric` | Telemetry and monitoring events |

Wildcard subscriptions:
- `agent.*` matches all single-segment agent topics
- `agent.#` matches all agent topics including multi-segment ones
- `tool.invoke.*` matches all tool invocations

## RabbitMQ Implementation Details

### Exchange Topology

```
rockbot (topic exchange)
├── rockbot.agent-host-a (queue) ← bound to: agent.task.*, agent.response.*
├── rockbot.tool-runner (queue) ← bound to: tool.invoke.*
└── rockbot.monitor (queue) ← bound to: #  (all messages)

rockbot.dlx (topic exchange)
├── rockbot.agent-host-a.dlq (queue)
└── rockbot.tool-runner.dlq (queue)
```

### Connection and Channel Strategy

- **One connection per process**: Connections are heavyweight (TCP + AMQP handshake). Shared via `RabbitMqConnectionManager`.
- **One channel per consumer**: Channels are lightweight but not thread-safe. Each subscription gets its own channel.
- **Publisher channel**: The publisher maintains its own channel, separate from consumer channels.
- **Prefetch**: Default prefetch count of 10 balances throughput and fairness.

### Envelope-to-AMQP Mapping

| Envelope Field | AMQP Property |
|---|---|
| MessageId | `BasicProperties.MessageId` |
| MessageType | `BasicProperties.Type` |
| CorrelationId | `BasicProperties.CorrelationId` |
| ReplyTo | `BasicProperties.ReplyTo` |
| Timestamp | `BasicProperties.Timestamp` |
| Source | Header `rb-source` |
| Destination | Header `rb-destination` |
| Custom Headers | Headers with `rb-` prefix |
| Body | Message body (bytes) |

The `rb-` prefix on custom headers avoids collisions with AMQP's own header fields.

### Retention and Expiry

A queue that only grows eventually fills the broker's disk. When it does, RabbitMQ's disk alarm blocks **every** publisher on the broker, and the whole swarm stops. Three kinds of queue would grow without a bound: dead-letter queues, per-process queues that outlive their process, and per-identity fan-out queues whose identity stops running. Both are capped in code by `RabbitMqSubscriber`, so the caps don't depend on Helm values, the Management API, or broker policies.

#### Dead-letter queue caps

Every `*.dlq` is declared with these queue arguments (`RabbitMq:DlqRetention`, Helm `rabbitmq.dlqRetention`):

| Argument | Default | Effect |
|---|---|---|
| `x-message-ttl` | 72 h (`MessageTtlMs`) | Dead-lettered messages expire |
| `x-max-length` | 10 000 (`MaxLength`) | Caps the message count |
| `x-max-length-bytes` | 256 MiB (`MaxLengthBytes`) | Caps total body size (LLM responses and attachments are large) |
| `x-overflow` | `drop-head` | The oldest messages are dropped, never new publishes rejected |

**Why queue arguments, not a policy.** RabbitMQ applies only one *user* policy per queue, the highest-priority match. A site policy matching `*.dlq` (HA, quorum, ...) would silently *replace* a retention policy. Queue arguments instead *combine* with whatever policy applies, and the stricter limit wins, the same way an operator policy behaves. No policy can lift these caps, and nothing has to be applied out-of-band. (The earlier Helm policy job never ran: it was gated on `managementApiBaseUrl`, which defaults to empty.)

**Shared DLQs never expire.** A DLQ has no consumer, so `x-expires` would delete it while its main queue is still in use. Only ephemeral DLQs (below) get `x-expires`.

**Migration.** Re-declaring an existing queue with different arguments fails with `406 PRECONDITION_FAILED`. When a DLQ declared before these caps existed (or with different caps) hits that error:

- If it is **empty**, it is deleted and re-declared with the configured arguments (logged at Information).
- If it is **not empty**, it is left as-is so no dead-letters are lost, and the subscription carries on. An **Error** is logged on every start, naming the queue, until someone inspects and purges it and restarts the process.

**Keep retention config identical** across every process that shares a vhost. A DLQ is re-declared by whichever process owns the subscription. If two processes disagree on the caps for the same queue, each start migrates it back and forth.

`dlqRetention.enabled: false` declares DLQs with no caps. Existing capped DLQs are migrated back by the same rule.

#### Ephemeral subscriptions

Some subscriptions belong to one process lifetime: each CLI run (`user-proxy.cli-{guid}*`), each A2A gateway request (`a2a-gw-{guid}`, `a2a-gw-status-{guid}`), and each UI pod's WorkIQ expiry listener (`ui.workiq.expired.{guid}`). These queues used to be durable with no expiry, so every run left a queue and DLQ behind.

These callers subscribe with `new SubscriptionOptions { Ephemeral = true }` (the CLI sets `UserProxyOptions.EphemeralQueues`):

- **On dispose**, the main queue is deleted and the DLQ is deleted if it is empty.
- **If the process dies first**, the broker deletes both queues after they have been idle for `RabbitMq:EphemeralQueueExpiry` (`x-expires`, default 24 h).
- **The main queue** also has `x-message-ttl` = `RabbitMq:EphemeralMessageTtl` (default 1 h). A reply nobody consumed within that window is dead-lettered.
- **An ephemeral DLQ has no consumer either**, so in a long-running process (the UI pod's WorkIQ listener) the broker may expire it after 24 h idle. Later dead-letters from that queue are then dropped until the DLQ is re-declared on the next reconnect or restart. This is acceptable for per-process notification queues.

Long-lived subscriptions (agent `{identity}.*` queues, Blazor's stable `user-proxy` identity) stay durable and shared.

`SubscriptionOptions` is a default-implemented overload on `IMessageSubscriber`. Providers whose subscriptions don't outlive the process (in-process) ignore `Ephemeral` and the retention options below.

#### Per-identity fan-out subscriptions

A queue named after an agent identity (`rockbot.{Agent}.*`) is durable and shared, so it outlives the agent. When it is bound to a fan-out topic, every other agent's traffic keeps arriving after the agent has stopped for good. `rockbot.{Agent}.discovery` is the case that grew: each live agent re-announces its card every 2 minutes, and the queues of retired runs reached 62 785 (`AdvisorCouncil`) and 44 340 (`ResearchAgent`) messages before #650.

`Ephemeral` is the wrong fix for these queues. It deletes the queue on dispose, and during a rolling restart the old pod's dispose would delete the queue that the replacement pod is already consuming from. Instead, three per-subscription options set retention without changing the queue's lifetime:

| Option | Queue argument | Effect |
|---|---|---|
| `MessageTtl` | `x-message-ttl` on the main queue | Messages older than this are discarded |
| `IdleExpiry` | `x-expires` on the main queue | The broker deletes the queue once it has had no consumer for this long |
| `DeadLetter = false` | no `x-dead-letter-*`, no DLQ declared | Expired and rejected messages are dropped |

On an `Ephemeral` subscription, `MessageTtl` and `IdleExpiry` override `RabbitMq:EphemeralMessageTtl` and `RabbitMq:EphemeralQueueExpiry`. A shared DLQ still never gets `x-expires`, even when its main queue has `IdleExpiry`.

**Turn off dead-lettering together with a short TTL.** RabbitMQ dead-letters expired messages whenever the queue has a dead-letter exchange. A short TTL without `DeadLetter = false` moves the backlog into the DLQ and sets off the DLQ growth alert.

**Discovery** (`AgentDiscoveryService.DiscoverySubscriptionOptions`): `MessageTtl` = 10 min (5 × the re-announce interval), `IdleExpiry` = 24 h, `DeadLetter = false`. A stopped agent's queue holds at most 10 minutes of announcements, and the broker deletes it a day after the agent stopped. Nothing is lost: on start, the directory is rebuilt from fresh announcements within one interval. An invalid card, which the handler dead-letters, is now dropped and logged as a warning.

**Migration.** The new arguments differ from the existing queue's, so the first start on the new version gets a 406 and deletes and recreates the main queue. Whatever was queued is dropped, which is fine for discovery. The old `rockbot.{Agent}.discovery.dlq` is no longer declared or fed. Delete it once it is empty (see the cleanup below).

**Audit of other fan-out subscriptions:**

- `agent.task.status` is bound by every A2A caller as `{Agent}.agent-task-status`. It has the same failure mode, at far lower volume, since it only carries traffic while tasks are running. Since #654 it is subscribed with `A2ACallerServiceCollectionExtensions.StatusSubscriptionOptions`: `MessageTtl` = 10 min, `IdleExpiry` = 24 h, `DeadLetter = false`. A status update is only useful while the caller is still tracking the task, and that tracking (`A2ATaskTracker`) is in memory, so a restart makes every queued update useless anyway. It migrates through the same 406 path as discovery.
- `AgentHost` topics take `SubscriptionOptions` through `AgentHostBuilder.SubscribeTo(topic, SubscriptionOptions)`. Every other topic on the host list is either point-to-point (`*.{agentName}`) or a work queue (`script.invoke`, the script runners), where a request whose consumer is down should wait, not expire. None of them need retention.
- `user.response` broadcast (`user-proxy.{ProxyId}`): the CLI is ephemeral, and Blazor is a long-running deployment with a stable identity.
- `council.research-reply.{pid}.{guid}` (AdvisorCouncil's `ResearchAgentInvoker`) is a per-process reply queue, and it was not ephemeral. It is now `Ephemeral = true`.

#### Alerting and broker disk limit

- **DLQ growth alert.** `DlqDepthReporter` publishes the `rockbot.messaging.dlq.depth` gauge (Prometheus `rockbot_messaging_dlq_depth`) every 60 s when `RabbitMq:ManagementApiBaseUrl` is set. Without it there is no gauge and no alert. The lhotkalake cluster alerts on this gauge with a Grafana alert rule (sister repo `lhotkalake-k8s`, `fleet/observability/grafana/values.yaml`): any DLQ above 100 messages for 15 min, or sustained growth over 30 min.
- **`disk_free_limit`.** RabbitMQ's default is 50 MB, so the alarm fires only when the disk is effectively full, with no time to react. The broker config lives outside this chart. Set it in the broker's `rabbitmq.conf` (or the cluster operator's `additionalConfig`):

  ```ini
  # absolute: the alarm fires with 1 GB still free
  disk_free_limit.absolute = 1GB
  # or relative to RAM (the RabbitMQ production recommendation)
  # disk_free_limit.relative = 1.0
  ```

#### One-time cleanup of orphaned queues

Queues orphaned before ephemeral subscriptions existed have no `x-expires`, so they stay until deleted. List the candidates (no consumers), review them, then delete:

```bash
rabbitmqctl -p <vhost> list_queues name consumers messages \
  | awk '$2 == 0 && ($1 ~ /^rockbot\.user-proxy\.cli-/ || $1 ~ /^rockbot\.ui\.workiq\.expired/ || $1 ~ /^rockbot\.a2a-gw-/ || $1 ~ /^rockbot\.council\.research-reply\./ || $1 ~ /\.(discovery|agent-task-status)(\.dlq)?$/)'

# after reviewing the list:
rabbitmqctl -p <vhost> delete_queue <queue-name>
```

Verify the caps after a deploy:

```bash
rabbitmqctl -p <vhost> list_queues name arguments | grep '\.dlq'
```

## Testing Strategy

### Unit Tests (no RabbitMQ required)
- Envelope creation and field defaults
- JSON serialization round-tripping via extension methods
- DI registration verification
- Message ID uniqueness

### Integration Tests (RabbitMQ required)
- Controlled by `ROCKBOT_RABBITMQ_HOST` environment variable
- Use unique exchange names per test run to avoid cross-contamination
- Test publish → subscribe round-trip
- Test wildcard subscription matching
- Test dead-letter routing on handler rejection

### Future: In-Memory Provider
An in-memory implementation of `IMessagePublisher` and `IMessageSubscriber` will enable full integration testing without infrastructure dependencies. Messages route directly through in-process queues, preserving the same semantics (topic matching, acknowledgment) without the network.
