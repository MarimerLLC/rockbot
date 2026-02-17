# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build
dotnet build RockBot.sln

# Run all tests (unit only, no RabbitMQ needed)
dotnet test RockBot.sln

# Run all tests including integration tests (requires RabbitMQ)
ROCKBOT_RABBITMQ_HOST=localhost dotnet test RockBot.sln

# Run a single test by fully qualified name
dotnet test RockBot.sln --filter "FullyQualifiedName~MessageEnvelopeTests.Create_SetsDefaults"

# Run tests by category/class
dotnet test RockBot.sln --filter "ClassName~RabbitMqIntegrationTests"
```

## Architecture

RockBot is an **event-driven autonomous agent framework** built on .NET 10. It replaces traditional game-loop agent patterns with a **message-based, decoupled swarm architecture** where agents communicate exclusively via a message bus.

**Core design principle: "Nothing trusts the LLM."** Agents run in separate processes with no shared memory, communicating only through messages. This provides process isolation so LLM-generated code cannot access the host directly.

### Project Structure

- **`src/RockBot.Messaging.Abstractions/`** — Provider-agnostic messaging contracts (`IMessagePublisher`, `IMessageSubscriber`, `MessageEnvelope`, `MessageResult`)
- **`src/RockBot.Messaging.RabbitMQ/`** — RabbitMQ implementation with topic exchange, dead-letter queues, and per-consumer channels
- **`tests/RockBot.Messaging.Tests/`** — MSTest unit tests + RabbitMQ integration tests (gated by `ROCKBOT_RABBITMQ_HOST` env var)
- **`design/`** — Architecture docs, messaging design, security model, and open questions

### Messaging Design

`MessageEnvelope` is a sealed record carrying: MessageId, MessageType, CorrelationId, ReplyTo, Source, Destination, Timestamp, Body (`ReadOnlyMemory<byte>`), and Headers. The body is raw bytes for transport agnosticism; convenience extensions (`ToEnvelope<T>`/`GetPayload<T>`) handle JSON serialization via System.Text.Json with camelCase policy.

`MessageResult` (Ack/Retry/DeadLetter) enables manual acknowledgment — handlers explicitly decide message fate.

Topics use hierarchical dot-separated naming: `agent.task.*`, `llm.request`, `tool.invoke.*`, etc. Wildcard subscriptions are supported (`*` single-level, `#` multi-level).

### RabbitMQ Provider

- One connection per process (heavyweight), one channel per consumer (not thread-safe)
- Topic exchange `rockbot` with dead-letter exchange `rockbot.dlx`
- AMQP header mapping uses `rb-` prefix for custom headers (Source, Destination, user headers)
- DI registration via `services.AddRockBotRabbitMq(options => ...)` — registers connection manager, publisher, and subscriber as singletons

### Key Conventions

- **Nullable reference types** enabled — respect null-safety throughout
- **ImplicitUsings** enabled — no need for common using statements
- **Async-first** — all I/O is Task-based, no blocking calls
- **Rocks** (not Moq) is the mocking framework for tests
- Integration tests return `Assert.Inconclusive` when RabbitMQ is unavailable rather than failing
- Integration tests use unique exchange names per run to avoid cross-contamination
