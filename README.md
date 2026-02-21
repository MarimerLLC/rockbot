# RockBot

An event-driven autonomous agent framework.

<img width="200" height="200" alt="rockbot" src="https://github.com/user-attachments/assets/bdc07b74-eaca-4255-acf2-6522c29c91ba" />

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
| `RockBot.Host` | Agent host runtime — handler pipeline, profile loading, system prompt composition, dream service |
| `RockBot.Llm` | LLM integration via `Microsoft.Extensions.AI` with per-model behavior overrides |
| `RockBot.Memory` | Three-tier memory system — conversation (ephemeral), long-term (persistent), and working (session context) |
| `RockBot.Skills` | Learned skill storage, BM25 recall, usage tracking, and dream-based optimization |
| `RockBot.Tools` | Tool registry, invocation dispatch, and tool-guide discovery |
| `RockBot.Tools.Mcp` | MCP (Model Context Protocol) server proxy — discovery, inspection, and invocation |
| `RockBot.Tools.Web` | Web search (Brave API) and web browsing with GitHub API routing and auto-chunking |
| `RockBot.Tools.Rest` | Direct HTTP endpoint invocation as tools |
| `RockBot.Tools.Scheduling` | Scheduled task execution with configurable result presentation |
| `RockBot.Scripts.Remote` | Agent-side script delegation over the message bus |
| `RockBot.Scripts.Manager` | Trusted sidecar that creates ephemeral Kubernetes pods for script execution |
| `RockBot.Scripts.Container` | Kubernetes container runner — isolated Python pods with resource limits |
| `RockBot.Scripts.Local` | Local Python script runner for development (no Kubernetes needed) |
| `RockBot.Subagent` | In-process subagent spawning — isolated LLM loops, progress reporting, and whiteboard data handoff |
| `RockBot.A2A` | Agent-to-agent task delegation over the message bus |
| `RockBot.UserProxy.Blazor` | Blazor Server chat UI with markdown rendering, conversation replay, and feedback signals |
| `RockBot.UserProxy.Cli` | Console chat interface using Spectre.Console |
| `McpServer.OpenRouter` | Standalone MCP server exposing OpenRouter account and usage information |
| `RockBot.Telemetry` | OpenTelemetry integration (OTLP gRPC export) |
| `RockBot.Cli` | Unified host application — orchestrates all of the above as hosted services |

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

## Key features

### Memory (three tiers)

- **Conversation memory** — sliding window of recent turns per session (default 50), auto-cleaned after idle timeout. Ephemeral and in-process.
- **Long-term memory** — persistent file-based store organized by category. The framework automatically surfaces relevant entries each turn via BM25 keyword search against the user's message (delta injection — only unseen entries are added).
- **Working memory** — fast session-scoped cache for intermediate results. Tools can save and retrieve data during a conversation without polluting long-term storage.

### Skills

Skills are reusable knowledge documents the agent learns through experience and saves for future sessions. The system includes:

- **Automatic recall** — BM25 search runs every turn to surface relevant skills as the conversation evolves.
- **See-also cross-references** — when a skill is recalled, its `seeAlso` references are also surfaced, enabling serendipitous discovery of related skills the agent didn't know to look for.
- **Skill index** — a summary of all skills is injected at session start so the agent knows what it has.
- **Usage tracking** — invocation counts and last-used timestamps enable pruning of stale skills.
- **Dream-based optimization** — a background dream pass periodically consolidates related skills, prunes unused ones, and refines content based on accumulated experience.
- **Prefix cluster detection** — skills sharing a name prefix (e.g. `mcp/email`, `mcp/calendar`) are detected during consolidation; the dream cycle can create abstract parent guide skills (`mcp/guide`) that describe when to use each sibling.
- **Structural staleness** — sparse skills (very short content, not recently improved) are flagged in consolidation and included in the optimization pass proactively, not only after failures.

### Tool guides

Each tool subsystem (MCP, web, scripts, scheduling, memory, skills) registers a `IToolSkillProvider` that exposes a usage guide. The agent can call `list_tool_guides` and `get_tool_guide` to learn how to use a capability it hasn't encountered before, then save a skill so future sessions skip the learning step.

### MCP bridge

The agent hosts an embedded MCP bridge that connects to configured MCP servers at startup via SSE transport. Tools from all connected servers are registered and callable through `mcp_invoke_tool`. The agent can also discover, inspect, register, and unregister MCP servers at runtime.

### Web tools

- **Web search** — Brave Search API integration for real-time information retrieval.
- **Web browse** — fetches and converts web pages to markdown. Includes a specialized GitHub API provider that routes GitHub URLs through the REST API for cleaner results.
- **Auto-chunking** — large web pages are automatically split and saved into working memory so the agent can reference them across turns.

### Script execution

- **Remote (production)** — the agent publishes a `script.invoke` message to RabbitMQ. The Scripts Manager (a trusted sidecar with Kubernetes API access) creates an ephemeral Python 3.12-slim pod in the isolated `rockbot-scripts` namespace, runs the script, and returns the result. Pods have resource limits (500m CPU, 256Mi RAM by default), no network access, and are deleted immediately after use.
- **Local (development)** — runs Python scripts directly on the local machine, no Kubernetes required.

### Background subagents

The agent can spawn isolated in-process subagents to handle long-running or complex tasks without blocking the primary conversation or exhausting its tool-call iteration limit.

- **spawn_subagent** — launches a subagent with its own LLM tool loop, scoped working memory, and cancellation token. Returns a `task_id` immediately.
- **Progress reporting** — the subagent calls `report_progress` periodically; each update is delivered to the primary session as a synthetic user turn so the agent can relay it naturally.
- **Result delivery** — on completion, a `SubagentResultMessage` is published to the primary session and incorporated into the conversation.
- **Whiteboard** — a concurrent-safe shared scratchpad (`WhiteboardWrite`, `WhiteboardRead`, `WhiteboardList`, `WhiteboardDelete`) enables structured data handoff between the primary agent and subagents.
- **Concurrency limits** — configurable maximum concurrent subagents (default 3) with graceful rejection when the limit is reached.

### Scheduled tasks

The agent can schedule tasks for future execution. Results are delivered back through the message bus, with configurable presentation modes per model (summarize vs. verbatim output).

### Dream service

A background hosted service that runs periodically (configurable interval) to autonomously refine the agent's knowledge:

- **Memory consolidation** — finds duplicates, merges related entries, refines categories.
- **Anti-pattern mining** — scans Correction feedback for failure patterns and writes `anti-patterns/{domain}` memory entries (e.g. "Don't use X for Y — use Z instead"). These surface via BM25 alongside regular memories as actionable constraints.
- **Skill consolidation** — merges overlapping skills, prunes stale ones, populates `seeAlso` cross-references, and detects prefix clusters to propose abstract parent guide skills.
- **Skill optimization** — improves skills involved in poor sessions; also proactively expands structurally sparse skills (very short content) even without failure signals.
- **Skill gap detection** — scans conversation logs for recurring request patterns and creates new skills; uses cross-session term frequency as a stronger signal for patterns the agent hasn't formalized yet.
- **Implicit preference learning** — extracts durable user preferences from conversation patterns.

### Model-specific behaviors

Per-model behavior overrides are loaded from `model-behaviors/{model-prefix}/` and can include:

- **Additional system prompt** — model-specific guardrails appended to every request.
- **Pre-tool-loop prompt** — constraints injected before tool-calling iterations.
- **Hallucination nudges** — detects when a model claims to have called a tool without actually doing so.
- **Tool iteration limits** — tuned per model based on convergence characteristics.

### Agent identity

The agent's personality and behavior are defined by markdown documents on the data volume:

- **soul.md** — core identity, values, and personality (stable, authored by prompt engineers).
- **directives.md** — deployment-specific operational instructions.
- **style.md** — optional voice and tone polish.
- **memory-rules.md** — rules governing when and how memories are formed.
- **dream.md / skill-dream.md / skill-optimize.md** — prompts guiding the background dream service.
- **session-evaluator.md** — criteria for evaluating conversation quality.

---

## Deployment

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — for local development and building
- [Docker](https://www.docker.com/) — for building container images
- Kubernetes cluster with [Helm](https://helm.sh/) — for production deployment
- [RabbitMQ](https://www.rabbitmq.com/) running in or accessible from the cluster
- [Longhorn](https://longhorn.io/) (or another `ReadWriteOnce` storage class) — for the agent data PVC
- [Tailscale Kubernetes Operator](https://tailscale.com/kb/1236/kubernetes-operator) *(optional)* — for exposing the Blazor UI on your tailnet

---

### Build and test locally

```bash
# Build
dotnet build RockBot.slnx

# Unit tests only (no external dependencies)
dotnet test RockBot.slnx

# Unit tests + RabbitMQ integration tests
ROCKBOT_RABBITMQ_HOST=localhost dotnet test RockBot.slnx
```

---

### Container images

Four images are published to Docker Hub and must be built from the repo root:

| Image | Dockerfile | Purpose |
|---|---|---|
| `rockylhotka/rockbot-cli` | `deploy/Dockerfile.cli` | Agent host (RockBot.Cli) |
| `rockylhotka/rockbot-blazor` | `src/RockBot.UserProxy.Blazor/Dockerfile` | Blazor chat UI |
| `rockylhotka/rockbot-scripts-manager` | `Dockerfile.scripts-manager` | Trusted script execution sidecar |
| `rockylhotka/rockbot-openrouter-mcp` | `src/McpServer.OpenRouter/Dockerfile` | OpenRouter MCP server *(optional)* |

```bash
# Build all from the repo root
docker build -f deploy/Dockerfile.cli          -t rockylhotka/rockbot-cli:latest .
docker build -f src/RockBot.UserProxy.Blazor/Dockerfile -t rockylhotka/rockbot-blazor:latest .
docker build -f Dockerfile.scripts-manager     -t rockylhotka/rockbot-scripts-manager:latest .
docker build -f src/McpServer.OpenRouter/Dockerfile -t rockylhotka/rockbot-openrouter-mcp:latest .

# Push
docker push rockylhotka/rockbot-cli:latest
docker push rockylhotka/rockbot-blazor:latest
docker push rockylhotka/rockbot-scripts-manager:latest
docker push rockylhotka/rockbot-openrouter-mcp:latest
```

---

### Kubernetes deployment (Helm)

The Helm chart at `deploy/helm/rockbot` deploys the full stack into two namespaces:

| Namespace | Contents |
|---|---|
| `rockbot` | Agent, Blazor UI, Scripts Manager, OpenRouter MCP *(optional)*, ConfigMap, Secret |
| `rockbot-scripts` | Ephemeral Python execution pods (created on demand) |

#### 1. Create your values file

```bash
cp deploy/values.personal.example.yaml deploy/values.personal.yaml
```

Edit `deploy/values.personal.yaml` — it is gitignored and must never be committed:

```yaml
rabbitmq:
  hostName: "rabbitmq-svc.rabbitmq.svc.cluster.local"
  userName: "rockbot"

secrets:
  create: true
  azureAI:
    endpoint: "https://<your-resource>.openai.azure.com/"
    key: "<your-api-key>"
    deploymentName: "<your-deployment-name>"
  webTools:
    apiKey: "<your-brave-search-api-key>"
  rabbitmq:
    password: "<your-rabbitmq-password>"
  # openRouter:
  #   apiKey: "<your-openrouter-management-api-key>"

blazor:
  tailscale:
    hostname: "rockbot"   # exposes the UI at http://rockbot on your tailnet

# Optional — enable the OpenRouter MCP server
# openrouterMcp:
#   enabled: true

# Optional — configure OpenTelemetry export
# telemetry:
#   enabled: true
#   otlpEndpoint: "http://alloy.monitoring.svc.cluster.local:4317"

# Recommended — set your local IANA timezone so the agent uses your time, not UTC
# agent:
#   timezone: "America/Chicago"
```

#### 2. Install or upgrade

```bash
helm upgrade --install rockbot deploy/helm/rockbot \
  -f deploy/values.personal.yaml \
  --create-namespace
```

#### 3. Restart pods to pick up new images

After pushing updated images (all tagged `latest`):

```bash
kubectl rollout restart deployment/rockbot-agent \
                          deployment/rockbot-blazor \
                          deployment/rockbot-scripts-manager \
  -n rockbot
```

---

### What Helm deploys

#### Agent (`rockbot-agent`)

- Runs `RockBot.Cli` as a single replica with `strategy: Recreate` — only one agent may run at a time.
- Mounts a 10 Gi Longhorn PVC at `/data/agent` containing soul, directives, memory, skills, and `mcp.json`.
- An **init container** seeds the PVC with default agent files from the image on first start (existing files are never overwritten). Per-model behavior prompts are also seeded per-file so customizations survive upgrades.
- Runs with a dedicated ServiceAccount (`rockbot-agent`) that has **no Kubernetes API permissions** (`automountServiceAccountToken: false`). Script execution is delegated to the Scripts Manager via RabbitMQ — the agent never touches the Kubernetes API directly.

#### Scripts Manager (`rockbot-scripts-manager`)

- Runs `RockBot.Scripts.Manager` as the **trusted sidecar** for script execution.
- Has its own ServiceAccount (`rockbot-scripts-manager`) with `automountServiceAccountToken: true`.
- Bound via a cross-namespace `RoleBinding` to the `rockbot-script-runner` Role in the `rockbot-scripts` namespace.

**RBAC — least privilege:**

| Resource | Verbs | Why |
|---|---|---|
| `pods` | `create`, `get`, `list`, `watch`, `delete` | Spin up and clean up ephemeral script containers |
| `pods/log`, `pods/status` | `get` | Stream script output back to the agent |

The agent has *no* pod permissions. A compromised agent cannot create or access Kubernetes pods.

#### Blazor UI (`rockbot-blazor`)

- Runs `RockBot.UserProxy.Blazor` on port 8080 with liveness and readiness probes.
- Exposed on your Tailscale network via the Tailscale Kubernetes Operator using the hostname set in `blazor.tailscale.hostname`.
- Receives only the RabbitMQ credentials it needs — not the full agent ConfigMap.
- Supports markdown rendering, conversation history replay on reconnect, input history, multiline input (Shift+Enter), and implicit feedback signals.

#### OpenRouter MCP (`rockbot-openrouter-mcp`) *(optional)*

- Deployed when `openrouterMcp.enabled: true` in your values file.
- Exposes read-only tools for querying OpenRouter account credits, available models, API keys, and generation logs.
- Connected to the agent automatically via `mcp.json`.

#### Scripts namespace (`rockbot-scripts`)

- Created and owned by the Helm chart.
- Ephemeral Python pods are launched here by the Scripts Manager and deleted immediately after the script completes.
- The agent namespace (`rockbot`) has no permissions in this namespace.

---

### Agent data volume

The agent PVC holds all persistent state:

```
/data/agent/
├── soul.md               # Core identity and values
├── directives.md         # Behavioral rules and constraints
├── style.md              # Tone and communication style
├── memory-rules.md       # Rules governing memory formation
├── dream.md              # Memory consolidation prompts
├── skill-dream.md        # Skill acquisition prompts
├── skill-optimize.md     # Skill consolidation and pruning prompts
├── session-evaluator.md  # Conversation quality evaluation criteria
├── mcp.json              # MCP server connections
├── model-behaviors/      # Per-model prompt overrides
│   └── deepseek/         # (e.g. additional-system-prompt.md, pre-tool-loop-prompt.md)
├── memory/               # Long-term memory entries (organized by category)
└── skills/               # Learned skills (JSON documents)
```

To replace the default files with your own after first deployment:

```bash
# Find the agent pod
kubectl get pods -n rockbot -l app=rockbot-agent

# Copy your local agent directory to the PVC
kubectl cp src/RockBot.Cli/agent/ <pod-name>:/data/agent/ -n rockbot

# Copy a custom mcp.json
kubectl cp src/RockBot.Cli/mcp.json <pod-name>:/data/agent/mcp.json -n rockbot
```

---

### MCP tool configuration

Edit `/data/agent/mcp.json` on the PVC (or copy it in as shown above) to configure MCP server connections. The embedded MCP bridge discovers and registers available tools automatically on startup. Servers can also be registered and unregistered at runtime through the agent's `mcp_register_server` and `mcp_unregister_server` tools.

---

### Telemetry (optional)

Set the following in your `values.personal.yaml` to enable OpenTelemetry export:

```yaml
telemetry:
  enabled: true
  otlpEndpoint: "http://alloy.monitoring.svc.cluster.local:4317"
  serviceName: rockbot
```

Logs stream to stdout and are picked up automatically by Promtail or any standard log collector. Filter in Grafana with `{namespace="rockbot"}`.

---

## Subsystem documentation

Deep-dive documentation for individual subsystems lives in [`docs/`](docs/):

| Document | Contents |
|---|---|
| [`docs/skills.md`](docs/skills.md) | Skills data model, BM25 recall, see-also, dream consolidation, prefix clusters, optimization, gap detection |
| [`docs/memory.md`](docs/memory.md) | Three-tier memory architecture, long-term storage, anti-patterns, working memory, dream passes |
| [`docs/dream-service.md`](docs/dream-service.md) | Dream cycle passes, scheduling, directive files, LLM response contracts, configuration |
| [`docs/blazor-ui.md`](docs/blazor-ui.md) | Blazor chat UI architecture, UserProxyService, feedback, history replay, deployment |
| [`docs/messaging.md`](docs/messaging.md) | MessageEnvelope, publisher/subscriber interfaces, RabbitMQ provider, topics, trust levels, trace propagation |
| [`docs/agent-host.md`](docs/agent-host.md) | AgentHostBuilder, pipeline, identity, profile, conversation memory, session evaluation, LLM client, data volume layout |
| [`docs/tools.md`](docs/tools.md) | Tool execution model, IToolRegistry, MCP bridge, web search/browse, REST, scheduling, script execution, OpenRouter MCP |

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
