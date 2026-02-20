# Open Questions

Design decisions that needed further thought or experimentation. All questions have been resolved — see **Decision** entries below.

## Agent Host

### Handler Pipeline: Chain vs. Single Dispatch?

The chain-of-responsibility model (like ASP.NET Core middleware) is more flexible but adds complexity. Is it worth it for the initial implementation, or should we start with simple single-dispatch and refactor later?

**Decision**: Chain-of-responsibility. The middleware pattern is well-understood in the .NET ecosystem, and cross-cutting concerns (logging, error handling, rate limiting) are needed immediately.

### Hosted Service or Standalone?

Should agent hosts be `IHostedService` implementations that run inside a generic .NET host, or standalone executables?

**Decision**: `IHostedService`. Built-in configuration, logging, DI, and graceful shutdown. Agents are background services in a standard `Host.CreateDefaultBuilder()` application.

## Scripting

### Which Languages to Support?

LLMs generate better Python and TypeScript than anything else. But these are harder to sandbox than WASM.

Options:
- **Python only**: Simplest. Most LLMs are best at Python. Container-based execution is straightforward.
- **Python + TypeScript**: Broader coverage. Node.js containers are well-understood.
- **WASM-first**: Most secure. But LLMs are poor at generating WASM-compatible code directly. Would need a compilation step.
- **Python in containers, with WASM for trusted/simple scripts**: Hybrid approach.

**Decision**: Python only, in containers. LLMs generate the best Python. Add more languages later if needed.

### Script Output Format

How should scripts return results? Options:
- Stdout (simple but unstructured)
- JSON on stdout (structured, parseable)
- Write to a designated output file (supports binary output)
- All of the above, with a convention for which to use

**Decision**: JSON on stdout as the primary mechanism, with file output for binary data (images, files, etc.).

## State Management

### Where Does Conversation State Live?

Options:
- **Redis**: Fast, ephemeral. Good for active conversations.
- **SQLite/PostgreSQL**: Durable, queryable. Good for history and audit.
- **Event sourcing**: Reconstruct state from message history. Elegant but complex.
- **In the messages themselves**: Pass full context with every message. Stateless agents, but messages grow large.

**Decision**: In the messages themselves — stateless agents. Most agents will be external (in other containers or environments, possibly authored by different teams). Requiring shared infrastructure (Redis, a database) beyond the message bus creates coupling and operational burden. The message bus is the only shared infrastructure. The orchestrating agent owns responsibility for accumulating conversation context and deciding what to forward to downstream agents ("need to know" principle). Worker agents are truly simple: receive a message with everything needed, do work, return a result.

### Context Window Management

LLM context windows are finite. As conversations grow, we need to manage what goes into each LLM call. This is really the LLM handler's responsibility, but the state store needs to support it.

Options:
- Sliding window of recent messages
- Summarization of older messages (using the LLM itself)
- RAG over conversation history
- Combination

**Decision**: Sliding window with summarization. The orchestrator handles summarization before publishing — it owns the conversation history and is responsible for managing context size.

## A2A Protocol

### Transport for A2A

The A2A spec assumes HTTP. We want to run it over RabbitMQ. This means:
- Defining an A2A message envelope that maps to our `MessageEnvelope`
- Implementing A2A task lifecycle (create, status, result, cancel) as message types
- Agent discovery via message bus rather than HTTP endpoints

**Decision**: Map A2A over RabbitMQ. Define A2A task lifecycle (create/status/result/cancel) as `MessageEnvelope` payloads routed via topics.

### Agent Discovery

How do agents find each other? Options:
- **Static configuration**: Each agent knows about the others via config. Simple but inflexible.
- **Registry service**: A dedicated agent that maintains a directory. Agents register on startup.
- **Topic-based discovery**: Agents subscribe to a `discovery.announce` topic and broadcast capabilities. Other agents listen and maintain a local cache.

**Decision**: Topic-based discovery. Agents broadcast capabilities on `discovery.announce` topic at startup. Uses existing message infrastructure — no separate registry service required.

## Infrastructure

### RabbitMQ vs. Starting with In-Memory

Should we develop against a real RabbitMQ instance or build the in-memory provider first?

**Decision**: Develop against real RabbitMQ. The homelab cluster already runs it. The in-memory provider is important for CI and unit tests but shouldn't be the primary development target — too easy to miss real-world edge cases.

### Observability

OpenTelemetry is the obvious choice. But how much instrumentation in v1?

**Decision**: OTel traces + structured logging from the start, exporting to existing Loki/Grafana in the cluster. Distributed tracing (spans, trace context propagation in message headers) and structured logging wired up in Phase 2. Custom metrics (counters, histograms) deferred to Phase 6.

## Standing Conventions

### Configuration

Use the standard .NET 10 configuration stack: `appsettings.json`, environment variables, `dotnet user-secrets` for local dev, Kubernetes Secrets for deployment. No custom config mechanisms.

### Console Applications

Use `Spectre.Console` for argument parsing, prompts, and CLI output in any console applications.
