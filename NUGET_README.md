# RockBot Framework SDK

RockBot is an event-driven autonomous agent framework for building **multi-agent AI systems** on .NET. Agents communicate exclusively through a message bus with full process isolation — no shared memory, no direct method calls, and no LLM-generated code running in-process with the host.

## Packages

### Messaging

| Package | Description |
|---|---|
| `RockBot.Messaging.Abstractions` | Transport-agnostic contracts: `IMessagePublisher`, `IMessageSubscriber`, `MessageEnvelope`, `MessageResult` |
| `RockBot.Messaging.RabbitMQ` | RabbitMQ provider with topic exchanges, dead-letter queues, and per-consumer channels |
| `RockBot.Messaging.InProcess` | In-memory bus for local development and testing |

### Host

| Package | Description |
|---|---|
| `RockBot.Host.Abstractions` | Agent host abstractions including agent profiles and configuration contracts |
| `RockBot.Host` | Agent host runtime — handler pipeline, profile loading, system prompt composition |

### LLM

| Package | Description |
|---|---|
| `RockBot.Llm.Abstractions` | LLM client contracts and message types |
| `RockBot.Llm` | LLM integration via `Microsoft.Extensions.AI` with three-tier model routing |

### Memory and Skills

| Package | Description |
|---|---|
| `RockBot.Memory` | Three-tier memory system: conversation (ephemeral), long-term (persistent), working (session) |
| `RockBot.Skills` | Learned skill storage, BM25 recall, usage tracking, and dream-based optimization |

### Tools

| Package | Description |
|---|---|
| `RockBot.Tools.Abstractions` | Tool registry and invocation contracts |
| `RockBot.Tools` | Tool registry, invocation dispatch, and tool-guide discovery |
| `RockBot.Tools.Scheduling` | Scheduled task execution with configurable result presentation |
| `RockBot.Tools.Mcp` | Model Context Protocol (MCP) server proxy, discovery, and invocation |
| `RockBot.Tools.Web` | Web search (Brave API) and browsing with auto-chunking |

### Scripts

| Package | Description |
|---|---|
| `RockBot.Scripts.Abstractions` | Script execution abstractions |
| `RockBot.Scripts.Local` | Local Python script runner for development |
| `RockBot.Scripts.Remote` | Agent-side script delegation over the message bus |
| `RockBot.Scripts.Container` | Kubernetes container runner for isolated script execution |

### Agents

| Package | Description |
|---|---|
| `RockBot.A2A.Abstractions` | Agent-to-agent communication abstractions |
| `RockBot.A2A` | Agent-to-agent task delegation with Google A2A protocol support |
| `RockBot.Subagent` | In-process subagent spawning with progress reporting and data handoff |
| `RockBot.ServiceSearch` | Unified BM25 keyword search across A2A agents and MCP servers |

### User Proxy

| Package | Description |
|---|---|
| `RockBot.UserProxy.Abstractions` | User interaction contracts |
| `RockBot.UserProxy` | User proxy implementation for agent-user interaction over the message bus |

### Telemetry

| Package | Description |
|---|---|
| `RockBot.Telemetry` | OpenTelemetry integration with OTLP gRPC export |

## Getting Started

Install the packages you need for your agent:

```bash
dotnet add package RockBot.Host
dotnet add package RockBot.Messaging.RabbitMQ
dotnet add package RockBot.Llm
dotnet add package RockBot.Tools
```

## Resources

- [GitHub Repository](https://github.com/MarimerLLC/rockbot) — source code, issues, and contributions
- [Getting Started with Docker Desktop](https://github.com/MarimerLLC/rockbot/blob/main/docs/getting-started-docker-desktop.md) — run an agent locally in minutes
- [Architecture Documentation](https://github.com/MarimerLLC/rockbot/tree/main/docs) — deep-dive docs for each subsystem
- [Contributing Guide](https://github.com/MarimerLLC/rockbot/blob/main/README.md#contributing) — how to contribute

## License

[MIT](https://github.com/MarimerLLC/rockbot/blob/main/LICENSE)
