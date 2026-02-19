# RockBot

An event-driven autonomous agent framework.

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

Three images are published to Docker Hub and must be built from the repo root:

| Image | Dockerfile | Purpose |
|---|---|---|
| `rockylhotka/rockbot-cli` | `deploy/Dockerfile.cli` | Agent host (RockBot.Cli) |
| `rockylhotka/rockbot-blazor` | `src/RockBot.UserProxy.Blazor/Dockerfile` | Blazor chat UI |
| `rockylhotka/rockbot-scripts-manager` | `Dockerfile.scripts-manager` | Trusted script execution sidecar |

```bash
# Build all three from the repo root
docker build -f deploy/Dockerfile.cli          -t rockylhotka/rockbot-cli:latest .
docker build -f src/RockBot.UserProxy.Blazor/Dockerfile -t rockylhotka/rockbot-blazor:latest .
docker build -f Dockerfile.scripts-manager     -t rockylhotka/rockbot-scripts-manager:latest .

# Push
docker push rockylhotka/rockbot-cli:latest
docker push rockylhotka/rockbot-blazor:latest
docker push rockylhotka/rockbot-scripts-manager:latest
```

---

### Kubernetes deployment (Helm)

The Helm chart at `deploy/helm/rockbot` deploys the full stack into two namespaces:

| Namespace | Contents |
|---|---|
| `rockbot` | Agent, Blazor UI, Scripts Manager, ConfigMap, Secret |
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

blazor:
  tailscale:
    hostname: "rockbot"   # exposes the UI at http://rockbot on your tailnet
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
- An **init container** seeds the PVC with default agent files from the image on first start (existing files are never overwritten).
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

#### Scripts namespace (`rockbot-scripts`)

- Created and owned by the Helm chart.
- Ephemeral Python pods are launched here by the Scripts Manager and deleted immediately after the script completes.
- The agent namespace (`rockbot`) has no permissions in this namespace.

---

### Agent data volume

The agent PVC holds all persistent state:

```
/data/agent/
├── soul.md          # Core identity and values
├── directives.md    # Behavioral rules and constraints
├── style.md         # Tone and communication style
├── memory-rules.md  # Rules governing memory formation
├── dream.md         # Background reasoning prompts
├── skill-dream.md   # Skill acquisition prompts
├── mcp.json         # MCP server connections
├── memory/          # Long-term memory entries
└── skills/          # Saved skills
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

Edit `/data/agent/mcp.json` on the PVC (or copy it in as shown above) to configure MCP server connections. The MCP bridge discovers and registers available tools automatically on startup.

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
