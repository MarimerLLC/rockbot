# RockBot

An event-driven autonomous agent framework for .NET 10.

---

## What is RockBot?

RockBot is a framework for building **multi-agent AI systems** where agents communicate exclusively through a message bus. There is no shared memory, no direct method calls between agents, and no LLM-generated code running in-process with the host.

Each agent is an isolated process that reacts to messages, invokes tools, calls LLMs, delegates work to other agents, and emits responses — all via a topic-based pub/sub message bus backed by RabbitMQ (or an in-process bus for local development).

### Core components

| Project | Purpose |
|---|---|
| `RockBot.Messaging.Abstractions` | Transport-agnostic contracts (`IMessagePublisher`, `IMessageSubscriber`, `MessageEnvelope`) |
| `RockBot.Messaging.RabbitMQ` | RabbitMQ provider with topic exchanges and dead-letter queues |
| `RockBot.Messaging.InProcess` | In-memory bus for local development and testing |
| `RockBot.Host` | Agent host runtime — receives messages and dispatches through the handler pipeline |
| `RockBot.Llm` | LLM integration via `Microsoft.Extensions.AI` |
| `RockBot.Tools` / `RockBot.Tools.Mcp` | Tool invocation — REST and MCP (Model Context Protocol) |
| `RockBot.Scripts.Container` | Ephemeral script execution in isolated Kubernetes containers |
| `RockBot.A2A` | Agent-to-agent task delegation over the message bus |
| `RockBot.Cli` | Unified host application — runs agents as hosted services |

---

## Why RockBot?

Most AI agent frameworks run LLM-generated code in the same process as the host application. This creates serious security and coupling problems:

- **LLM code can access the host directly** — file system, network, secrets, everything
- **Swapping LLM providers or tool backends** requires invasive changes throughout the codebase
- **One runaway agent** can crash or compromise the entire system
- **Scaling individual components** is impractical when everything runs together

RockBot is built around four foundational design goals:

### Separation of concerns

Every responsibility in the system has a clear owner with a well-defined boundary. Agents handle reasoning. The message bus handles routing. Tool bridges handle execution. LLM providers handle inference. None of these cross into each other's domain — they communicate only through typed messages. This makes each layer independently testable, replaceable, and understandable.

### Isolation of execution

LLM-generated code never runs in the same process as the host. Agents run in **separate processes** with no shared memory. Scripts execute in **ephemeral Kubernetes containers** that are discarded immediately after use. A compromised or runaway agent cannot access the host, read its secrets, or affect other agents. Failure is contained by design.

### Principle of least privilege

Each component knows only what it needs to do its job. Agents receive only the messages addressed to them. Tool bridges expose only the tools they are explicitly configured to serve. Scripts run in containers with no network access, no persistent storage, and no credentials. No component accumulates capabilities or context beyond its immediate task.

### Cloud-native by design

RockBot assumes it will run in a distributed, containerized environment. Agents are **stateless** — state lives in messages or external stores, never in process memory. Components **scale independently** — add more LLM workers without touching tool bridges, or scale script runners without restarting agents. The message bus provides **back-pressure, dead-letter queues, and durability** so the system degrades gracefully under load. Configuration flows in from the environment, secrets from Kubernetes Secrets, and observability out through OpenTelemetry.

The result is a swarm of agents that coordinate through messages, where the failure or compromise of any single component cannot cascade system-wide.

---

## Deployment

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [RabbitMQ](https://www.rabbitmq.com/download.html) (or Docker: `docker run -d -p 5672:5672 rabbitmq:4`)
- Kubernetes cluster (required only for ephemeral script execution)

### Build

```bash
dotnet build RockBot.slnx
```

### Run tests

Unit tests only (no external dependencies):

```bash
dotnet test RockBot.slnx
```

Unit tests + RabbitMQ integration tests:

```bash
ROCKBOT_RABBITMQ_HOST=localhost dotnet test RockBot.slnx
```

### Configuration

RockBot uses the standard .NET configuration stack. Create an `appsettings.json` alongside `RockBot.Cli` or use environment variables:

```json
{
  "RabbitMq": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest"
  }
}
```

For local secrets (do not commit credentials):

```bash
dotnet user-secrets set "RabbitMq:Password" "your-password" --project src/RockBot.Cli
```

For production, inject secrets via Kubernetes Secrets as environment variables.

### Run the agent host

```bash
dotnet run --project src/RockBot.Cli
```

### Agent profiles

Each agent is configured through three markdown files in its working directory:

| File | Purpose |
|---|---|
| `soul.md` | Core identity, values, and goals |
| `directives.md` | Behavioral rules and constraints |
| `style.md` | Tone, formatting, and communication style |

These files are human-readable, version-controlled, and composed at runtime into the agent's system prompt.

### MCP tool configuration

Place an `mcp.json` file alongside `RockBot.Cli` to configure MCP server connections. The MCP bridge will discover and register available tools automatically.

---

## Contributing

Contributions are welcome. Please open an issue before starting significant work so we can discuss the approach.

### Getting started

1. Fork the repository and create a branch from `main`
2. Make your changes with tests covering new behavior
3. Ensure all tests pass: `dotnet test RockBot.slnx`
4. Open a pull request with a clear description of what changed and why

### Guidelines

- **Keep it simple.** Avoid over-engineering. Add only what the current task requires.
- **Async-first.** All I/O must be `Task`-based — no blocking calls.
- **Nullable reference types are enabled.** Respect null-safety throughout.
- **Use Rocks for mocking**, not Moq.
- **Integration tests** should return `Assert.Inconclusive` when RabbitMQ is unavailable, not fail.
- **Console apps** use `Spectre.Console` for argument parsing and output.
- **Configuration** goes through the standard .NET configuration stack — no custom mechanisms.

### Code of conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold it.

---

## Acknowledgements

RockBot is inspired by [OpenClaw](https://openclaw.ai/) and [NanoBot](https://github.com/HKUDS/nanobot), both of which explored multi-agent coordination and tool-augmented LLM systems. RockBot builds on those ideas with a stronger emphasis on process isolation, least-privilege execution, and cloud-native deployment.

---

## License

[MIT](LICENSE)
