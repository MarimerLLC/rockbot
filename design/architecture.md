# RockBot Design

RockBot is an event-driven autonomous agent framework built on .NET 10. It prioritizes isolation, extensibility, and message-based communication over the monolithic "game loop" approach used by most existing agent frameworks.

## Core Philosophy

Most autonomous agent frameworks (OpenClaw, Nanobot, etc.) run in a tight loop: receive input → call LLM → execute tools → repeat. This creates several problems:

- **Security**: LLM-generated code runs in-process with the host, giving it access to everything the host can touch.
- **Coupling**: Agents, tools, and LLM interactions are tightly bound. Swapping providers or adding capabilities requires invasive changes.
- **Scalability**: A single-process loop can't distribute work across machines or scale individual components independently.
- **Reliability**: One bad tool call or runaway script can take down the entire system.

RockBot takes a different approach: **a swarm of isolated agents communicating via messages**. Each agent is a small, focused service that reacts to events. The "loop" lives in the messaging infrastructure, not in application code.

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                   Message Bus                        │
│              (RabbitMQ / Azure SB)                   │
│                                                      │
│  Topics: agent.task, agent.response, tool.invoke,   │
│          tool.result, llm.request, llm.response     │
└──────┬──────────┬──────────┬──────────┬─────────────┘
       │          │          │          │
  ┌────▼───┐ ┌───▼────┐ ┌───▼───┐ ┌───▼────────┐
  │ Agent  │ │ Agent  │ │  MCP  │ │  Script    │
  │ Host A │ │ Host B │ │ Bridge│ │  Runner    │
  └────────┘ └────────┘ └───────┘ └────────────┘
```

### Communication Protocols

RockBot supports three communication patterns, used in combination:

- **A2A (Agent-to-Agent)**: Task delegation between agents, preferably running over a queued transport like RabbitMQ rather than direct HTTP. This gives durability, back-pressure, and decoupling.
- **MCP (Model Context Protocol)**: Tool discovery and invocation. RockBot agents can be MCP clients (calling tools) or can bridge to existing MCP servers.
- **REST**: Direct HTTP calls for external APIs and services. Agents can generate and execute HTTP requests against arbitrary endpoints.

### Key Principle: No Game Loop

Instead of a polling loop, RockBot uses a true event-driven model. The host pulls messages from queues and dispatches them through a DI-based handler pipeline. Agent logic is purely reactive — a collection of event handlers, not a loop body.

## Agent Host Design

The Agent Host is the runtime that executes agent logic. It is deliberately minimal: its job is to receive messages, dispatch them to handlers, and send outgoing messages.

### Event Handler Pipeline

Inspired by ASP.NET Core middleware and MediatR, events flow through a pipeline of registered handlers:

```csharp
IEventHandler<UserMessage>
IEventHandler<A2AMessage>
IEventHandler<ToolResult>
IEventHandler<LlmResponse>
IEventHandler<TimerTick>
```

Each handler:
- Receives an event and a context
- Can produce zero or more new events
- Is registered via standard .NET DI
- Is stateless (state lives in the message context or an external store)

The LLM is not special — it's just another handler. It receives accumulated context, calls the model API, and emits tool-call events or response events.

### Handler Chain vs. Single Dispatch

Two models under consideration:

1. **Single dispatch**: Each event type maps to one handler. Simple, predictable.
2. **Chain of responsibility**: Multiple handlers can process the same event type in sequence (like middleware). Enables cross-cutting concerns (logging, rate limiting, context enrichment) without modifying core handlers.

The chain model is more powerful and aligns with how ASP.NET Core works. A handler can choose to pass the event down the chain or short-circuit.

## Messaging Layer

### Abstraction

The messaging layer is provider-agnostic. The core abstractions are:

- **`IMessagePublisher`**: Publish a message to a topic.
- **`IMessageSubscriber`**: Subscribe to a topic pattern with a handler callback.
- **`MessageEnvelope`**: Immutable envelope carrying routing metadata (message ID, correlation ID, source, destination, timestamp, headers) and a byte payload.
- **`MessageResult`**: Handler return value controlling acknowledgment (Ack, Retry, DeadLetter).

### RabbitMQ Implementation

The first provider implementation uses RabbitMQ with topic exchanges:

- **Topic exchange** (`rockbot`): Supports wildcard routing (`agent.*`, `tool.#`).
- **Durable queues**: Each subscription gets its own named queue for reliable delivery.
- **Dead-letter exchange** (`rockbot.dlx`): Failed messages route to DLQ for inspection.
- **Per-consumer channels**: Following RabbitMQ best practices, each subscriber gets its own channel.
- **Manual acknowledgment**: Handlers control ack/nack/reject via the `MessageResult` return value.

### Future Providers

The abstraction is designed to support:

- **Azure Service Bus**: Topics and subscriptions map directly.
- **AWS SQS/SNS**: SNS topics with SQS queue subscriptions.
- **In-memory**: For testing and single-process development.

## Script Execution

Agents need the ability to generate and execute code. This is where most frameworks create security holes. RockBot's approach:

### Isolation Strategy

Scripts never run in-process with the agent host. Options under consideration:

1. **WASM (preferred for security)**: Run generated scripts in a Wasmtime/Extism sandbox. Strong isolation, limited capabilities by design. The downside is that LLMs are better at generating Python/TypeScript than WASM-compatible code.

2. **Container-based (preferred for compatibility)**: Spin up ephemeral containers in Kubernetes for script execution. Natural fit for a K8s homelab environment. Each script gets its own container with constrained resources and network access.

3. **Hybrid**: Use WASM for simple/trusted scripts, containers for complex or untrusted ones.

### Script Communication

Scripts communicate with the agent host exclusively via messages:
- Agent publishes a `script.invoke` event with the script content and parameters.
- Script runner executes in isolation, publishes a `script.result` event.
- Agent receives the result as another event in its handler pipeline.

No shared memory, no direct API calls back into the host.

## State Management

Each agent conversation/task needs state. Options:

- **Event sourcing over the message bus**: Replay events to reconstruct state. Clean, auditable, but adds complexity.
- **Redis**: Fast, simple key-value state store. Good for conversation context and short-lived state.
- **Database**: For durable, queryable state (task history, agent configurations).

The agent host should provide a state abstraction (`IAgentStateStore`) that handlers use, with pluggable backends.

## Project Structure

```
rockbot/
├── design/                              # Design documentation
├── src/
│   ├── RockBot.Messaging.Abstractions/  # Provider-agnostic messaging contracts
│   ├── RockBot.Messaging.RabbitMQ/      # RabbitMQ implementation
│   ├── RockBot.Host/                    # Agent host runtime (future)
│   ├── RockBot.Host.Abstractions/       # Event handler interfaces (future)
│   ├── RockBot.Llm.Abstractions/        # LLM provider contracts (future)
│   ├── RockBot.Llm.Anthropic/           # Claude integration (future)
│   ├── RockBot.Scripts.Abstractions/    # Script execution contracts (future)
│   ├── RockBot.Scripts.Container/       # K8s container runner (future)
│   └── RockBot.Mcp/                     # MCP client bridge (future)
├── tests/
│   └── RockBot.Messaging.Tests/         # Messaging unit + integration tests
└── RockBot.sln
```

## Implementation Roadmap

### Phase 1: Messaging Foundation (current)
- [x] Messaging abstractions (`IMessagePublisher`, `IMessageSubscriber`, `MessageEnvelope`)
- [x] RabbitMQ provider implementation
- [x] Unit and integration tests
- [ ] In-memory provider for testing

### Phase 2: Agent Host
- [ ] `IEventHandler<T>` interface and dispatcher
- [ ] Handler pipeline with chain-of-responsibility support
- [ ] DI registration and hosted service integration
- [ ] Basic agent context and conversation threading via correlation IDs

### Phase 3: LLM Integration
- [ ] LLM abstraction (`ILlmClient`)
- [ ] Anthropic/Claude provider
- [ ] LLM handler that converts events to prompts and emits tool calls
- [ ] Conversation history management

### Phase 4: Tool Execution
- [ ] MCP client bridge (leverage existing MCP Aggregator)
- [ ] REST endpoint invocation handler
- [ ] Script execution with container-based isolation

### Phase 5: A2A Protocol
- [x] A2A message types and task lifecycle
- [x] Agent discovery and capability advertisement
- [x] Multi-agent task delegation over RabbitMQ

### Phase 6: Hardening
- [ ] Observability (OpenTelemetry traces and metrics)
- [ ] Rate limiting and back-pressure
- [ ] Azure Service Bus provider
- [ ] Admin UI / dashboard
