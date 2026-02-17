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
| `llm.request` | Requests to LLM providers |
| `llm.response` | LLM completions and tool calls |
| `tool.invoke.*` | Tool/MCP invocation requests |
| `tool.result.*` | Tool execution results |
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
├── rockbot.llm-handler (queue) ← bound to: llm.request
├── rockbot.tool-runner (queue) ← bound to: tool.invoke.*
└── rockbot.monitor (queue) ← bound to: #  (all messages)

rockbot.dlx (topic exchange)
├── rockbot.agent-host-a.dlq (queue)
├── rockbot.llm-handler.dlq (queue)
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
