# Open Questions

Design decisions that need further thought or experimentation.

## Agent Host

### Handler Pipeline: Chain vs. Single Dispatch?

The chain-of-responsibility model (like ASP.NET Core middleware) is more flexible but adds complexity. Is it worth it for the initial implementation, or should we start with simple single-dispatch and refactor later?

**Leaning toward**: Start with chain. The middleware pattern is well-understood in the .NET ecosystem, and cross-cutting concerns (logging, error handling, rate limiting) will be needed almost immediately.

### Hosted Service or Standalone?

Should agent hosts be `IHostedService` implementations that run inside a generic .NET host, or standalone executables?

**Leaning toward**: `IHostedService`. This gives us built-in configuration, logging, DI, and graceful shutdown for free. Agents are just background services in a standard `Host.CreateDefaultBuilder()` application.

## Scripting

### Which Languages to Support?

LLMs generate better Python and TypeScript than anything else. But these are harder to sandbox than WASM.

Options:
- **Python only**: Simplest. Most LLMs are best at Python. Container-based execution is straightforward.
- **Python + TypeScript**: Broader coverage. Node.js containers are well-understood.
- **WASM-first**: Most secure. But LLMs are poor at generating WASM-compatible code directly. Would need a compilation step.
- **Python in containers, with WASM for trusted/simple scripts**: Hybrid approach.

**Leaning toward**: Python in containers initially. Add WASM later for performance-sensitive or trusted workloads.

### Script Output Format

How should scripts return results? Options:
- Stdout (simple but unstructured)
- JSON on stdout (structured, parseable)
- Write to a designated output file (supports binary output)
- All of the above, with a convention for which to use

**Leaning toward**: JSON on stdout as the primary mechanism, with file output for binary data.

## State Management

### Where Does Conversation State Live?

Options:
- **Redis**: Fast, ephemeral. Good for active conversations.
- **SQLite/PostgreSQL**: Durable, queryable. Good for history and audit.
- **Event sourcing**: Reconstruct state from message history. Elegant but complex.
- **In the messages themselves**: Pass full context with every message. Stateless agents, but messages grow large.

**Leaning toward**: Redis for active state, with periodic snapshots to a durable store. Event sourcing is appealing but probably overkill for v1.

### Context Window Management

LLM context windows are finite. As conversations grow, we need to manage what goes into each LLM call. This is really the LLM handler's responsibility, but the state store needs to support it.

Options:
- Sliding window of recent messages
- Summarization of older messages (using the LLM itself)
- RAG over conversation history
- Combination

**Leaning toward**: Sliding window with summarization. Simple and effective.

## A2A Protocol

### Transport for A2A

The A2A spec assumes HTTP. We want to run it over RabbitMQ. This means:
- Defining an A2A message envelope that maps to our `MessageEnvelope`
- Implementing A2A task lifecycle (create, status, result, cancel) as message types
- Agent discovery via message bus rather than HTTP endpoints

Is this a clean mapping, or are we fighting the spec? Need to prototype.

### Agent Discovery

How do agents find each other? Options:
- **Static configuration**: Each agent knows about the others via config. Simple but inflexible.
- **Registry service**: A dedicated agent that maintains a directory. Agents register on startup.
- **Topic-based discovery**: Agents subscribe to a `discovery.announce` topic and broadcast capabilities. Other agents listen and maintain a local cache.

**Leaning toward**: Topic-based discovery. It uses the existing message infrastructure and doesn't require a separate service.

## Infrastructure

### RabbitMQ vs. Starting with In-Memory

Should we develop against a real RabbitMQ instance or build the in-memory provider first?

**Decision**: Develop against real RabbitMQ. The homelab cluster already runs it. The in-memory provider is important for CI and unit tests but shouldn't be the primary development target — too easy to miss real-world edge cases.

### Observability

OpenTelemetry is the obvious choice. But how much instrumentation in v1?

**Leaning toward**: Minimal but meaningful. Trace IDs in message headers from day one (cheap to add, painful to retrofit). Full OTel integration (spans, metrics, exporters) can come later.
