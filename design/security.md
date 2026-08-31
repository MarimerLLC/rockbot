# Security Design

## Threat Model

The primary threat in autonomous agent systems is **LLM-directed code execution**. When an LLM can generate and execute arbitrary code, it effectively has the same privileges as the host process. This is how OpenClaw and similar frameworks get into trouble — the LLM runs in a context with broad access, and any prompt injection or hallucination can escalate to system compromise.

RockBot's security model is built on one principle: **nothing trusts the LLM**.

## Isolation Boundaries

### Agent Isolation

Each agent runs as a separate process (or container). Agents communicate exclusively via the message bus. There is no shared memory, no direct method calls, no in-process plugin loading.

This means:
- A compromised agent can only send messages. It cannot read another agent's memory or state.
- An agent crash does not cascade. The message bus redelivers unacknowledged messages to a healthy instance.
- Agents can run with minimal OS/container permissions. An agent that only calls an LLM API needs only outbound HTTPS. An agent that runs scripts needs container orchestration access but nothing else.

### Script Isolation

Generated scripts (Python, TypeScript, etc.) run in sandboxed environments:

1. **Container sandbox**: Each script execution gets an ephemeral container with:
   - No network access (or restricted to specific endpoints)
   - Read-only filesystem except for a designated output directory
   - CPU and memory limits
   - No access to the host's Kubernetes API or service account
   - Automatic termination after a timeout

2. **WASM sandbox** (future): For lightweight, trusted scripts:
   - No filesystem access
   - No network access
   - Memory-limited
   - Deterministic execution

### Message Bus Security

- **Authentication**: RabbitMQ connections use credentials. Each agent should have its own credentials with minimal permissions.
- **Authorization**: RabbitMQ vhosts and topic permissions restrict which agents can publish/subscribe to which topics.
- **Encryption**: TLS for connections in production.

## Principle of Least Privilege

| Component | Needs Access To | Does NOT Need |
|---|---|---|
| Agent Host | Message bus, state store | Script runtime, filesystem, K8s API |
| LLM Handler | LLM API (outbound HTTPS), message bus | Filesystem, other APIs |
| Script Runner | Container runtime, message bus | LLM API, state store, other agents |
| MCP Bridge | MCP servers, message bus | Script runtime, LLM API directly |
| Monitor | Message bus (read-only) | Everything else |

## Input Validation

All messages crossing agent boundaries are untrusted:

- **Schema validation**: Messages must conform to expected shapes. Reject malformed messages to DLQ.
- **Size limits**: Maximum message body size prevents memory exhaustion.
- **Rate limiting**: Per-agent publish rate limits prevent a compromised agent from flooding the bus.
- **Correlation ID validation**: Agents should only respond to correlation IDs they initiated, preventing replay attacks.

## LLM-Specific Mitigations

- **No direct tool execution**: The LLM handler emits tool-call events. A separate, purpose-built handler validates and executes them. The LLM never directly calls a tool.
- **Tool allowlisting**: Each agent has a configured list of permitted tools. Tool invocation requests for unlisted tools are rejected.
- **Script review** (optional): For high-risk operations, generated scripts can be routed to a human approval queue before execution.
- **Output sanitization**: LLM responses are treated as untrusted text. They are never evaluated as code, interpolated into shell commands, or used to construct file paths without validation.

## Secrets Management

- Agent credentials, API keys, and connection strings are injected via environment variables or a secrets provider (Kubernetes Secrets, HashiCorp Vault).
- Secrets are never passed through the message bus.
- Secrets are never included in LLM context or conversation history.
