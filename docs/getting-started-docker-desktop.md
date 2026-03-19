# Getting Started with Docker Desktop

Run RockBot locally with Docker Compose on Docker Desktop. This is the simplest way to experiment with the agent — chat with it, explore its memory and skills system, and get a feel for how it works.

This minimal setup runs three containers: **RabbitMQ** (message bus), the **RockBot Agent** (the brain), and the **Blazor UI** (the chat interface). No Kubernetes, no Helm, no MCP servers.

> **What you can do**: Chat, build long-term memory, learn skills, web search, use the dream consolidation service. MCP and A2A also work if you register HTTP endpoints (see [Connecting MCP servers and A2A agents](#connecting-mcp-servers-and-a2a-agents) below).
>
> **What you can't do** (without Kubernetes): Run sandboxed Python scripts (requires the scripts-manager pod with K8s API access).

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) installed and running
- An **OpenAI-compatible LLM API key** — [OpenRouter](https://openrouter.ai/) is the easiest option
- A **Brave Search API key** (free tier available) — [https://api.search.brave.com/](https://api.search.brave.com/)

## 1. Create the Docker Compose file

Create a `docker-compose.yml` in any directory:

```yaml
services:
  rabbitmq:
    image: rabbitmq:4-management
    hostname: rabbitmq
    ports:
      - "15672:15672"   # Management UI (optional, handy for debugging)
    environment:
      RABBITMQ_DEFAULT_USER: rockbot
      RABBITMQ_DEFAULT_PASS: rockbot
    volumes:
      - rabbitmq-data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "check_port_connectivity"]
      interval: 10s
      timeout: 5s
      retries: 10

  agent-init:
    image: rockylhotka/rockbot-agent:latest
    user: root
    entrypoint: ["/bin/sh", "-c"]
    command:
      - |
        set -e
        echo "Seeding agent data volume..."
        for f in soul.md directives.md subagent-directives.md style.md memory-rules.md \
                 dream.md skill-dream.md common-directives.md session-evaluator.md \
                 session-start.md heartbeat-patrol.md skill-optimize.md \
                 dlq-dream.md routing-dream.md; do
          src="/app/agent/$$f"
          dst="/data/agent/$$f"
          if [ -f "$$src" ] && [ ! -s "$$dst" ]; then
            echo "  Copying $$f"
            cp "$$src" "$$dst"
          fi
        done
        if [ -f /app/agent/well-known-agents.json ] && [ ! -s /data/agent/well-known-agents.json ]; then
          cp /app/agent/well-known-agents.json /data/agent/well-known-agents.json
        fi
        # Seed an empty mcp.json so the agent doesn't load the image default
        # (which references cluster-internal URLs that don't exist locally)
        if [ ! -f /data/agent/mcp.json ]; then
          echo '{"mcpServers":{}}' > /data/agent/mcp.json
        fi
        # Per-model behavior files
        for model_dir in /app/model-behaviors/*/; do
          [ -d "$$model_dir" ] || continue
          model_name=$$(basename "$$model_dir")
          mkdir -p "/data/agent/model-behaviors/$$model_name"
          for src in "$$model_dir"*; do
            [ -f "$$src" ] || continue
            dst="/data/agent/model-behaviors/$$model_name/$$(basename $$src)"
            if [ ! -s "$$dst" ]; then
              cp "$$src" "$$dst"
            fi
          done
        done
        mkdir -p /data/agent/memory /data/agent/skills /data/agent/conversations /data/agent/feedback
        # Docker volumes are owned by root, so the non-root agent user needs access
        chmod -R 777 /data/agent
        echo "Agent data volume ready."
    volumes:
      - agent-data:/data/agent

  agent:
    image: rockylhotka/rockbot-agent:latest
    depends_on:
      agent-init:
        condition: service_completed_successfully
      rabbitmq:
        condition: service_healthy
    environment:
      # ── RabbitMQ ──
      RabbitMq__HostName: rabbitmq
      RabbitMq__Port: "5672"
      RabbitMq__UserName: rockbot
      RabbitMq__Password: rockbot
      RabbitMq__VirtualHost: /
      # ── LLM (Balanced tier — required) ──
      LLM__Balanced__Endpoint: ${LLM_ENDPOINT:-https://openrouter.ai/api/v1}
      LLM__Balanced__ApiKey: ${LLM_API_KEY:?Set LLM_API_KEY in your .env file}
      LLM__Balanced__ModelId: ${LLM_MODEL_ID:-anthropic/claude-haiku-4.5}
      # ── Web search ──
      WebTools__ApiKey: ${BRAVE_API_KEY:?Set BRAVE_API_KEY in your .env file}
      # ── Agent data paths ──
      AgentProfile__BasePath: /data/agent
      Memory__BasePath: /data/agent/memory
      Skill__BasePath: /data/agent/skills
      McpBridge__ConfigPath: /data/agent/mcp.json
      ModelBehaviors__BasePath: /data/agent/model-behaviors
      # ── Timezone (set to your IANA timezone) ──
      Agent__Timezone: ${AGENT_TIMEZONE:-America/Chicago}
    volumes:
      - agent-data:/data/agent

  blazor:
    image: rockylhotka/rockbot-blazor:latest
    depends_on:
      rabbitmq:
        condition: service_healthy
    ports:
      - "8080:8080"
    environment:
      RabbitMq__HostName: rabbitmq
      RabbitMq__Port: "5672"
      RabbitMq__UserName: rockbot
      RabbitMq__Password: rockbot
      RabbitMq__VirtualHost: /

volumes:
  rabbitmq-data:
  agent-data:
```

## 2. Create the `.env` file

Create a `.env` file in the same directory as your `docker-compose.yml`:

```env
# REQUIRED — your OpenAI-compatible API key
LLM_API_KEY=sk-or-v1-your-openrouter-key-here

# REQUIRED — Brave Search API key
BRAVE_API_KEY=BSA-your-brave-key-here

# OPTIONAL — override the LLM endpoint and model
# LLM_ENDPOINT=https://openrouter.ai/api/v1
# LLM_MODEL_ID=anthropic/claude-haiku-4.5

# OPTIONAL — your IANA timezone (defaults to America/Chicago)
# AGENT_TIMEZONE=America/New_York
```

> **Do not commit the `.env` file to version control.** It contains your API keys.

## 3. Start everything

```bash
docker compose up -d
```

Wait about 30 seconds for RabbitMQ to become healthy and the agent to connect. You can follow the agent logs to see when it's ready:

```bash
docker compose logs -f agent
```

Look for log output indicating the agent has connected to RabbitMQ and is listening for messages.

## 4. Chat with the agent

Open your browser to **[http://localhost:8080](http://localhost:8080)** to access the Blazor chat UI.

Start a conversation — the agent will respond using your configured LLM. Try things like:

- Ask it a question (it will use web search if needed)
- Ask it to remember something ("remember that I prefer dark mode")
- Ask it to recall something ("what do you know about me?")
- Ask it about its skills or capabilities

## 5. Monitor and debug

**RabbitMQ Management UI**: [http://localhost:15672](http://localhost:15672) (login: `rockbot` / `rockbot`)

Browse queues, exchanges, and message rates to see the event-driven architecture in action.

**View logs**:

```bash
# Agent logs
docker compose logs -f agent

# All services
docker compose logs -f
```

## Customizing the agent

The agent's personality, directives, and behavior are defined by markdown files on the `agent-data` volume. To customize them:

```bash
# Find the volume mount path
docker volume inspect <your-directory>_agent-data

# Or copy a file into the running container
docker compose cp my-custom-soul.md agent:/data/agent/soul.md
```

Key files you can customize:

| File | Purpose |
|------|---------|
| `soul.md` | Agent identity and personality |
| `directives.md` | Operating instructions and workflow patterns |
| `style.md` | Voice and tone |
| `memory-rules.md` | How the agent forms and manages memories |

The agent hot-reloads these files via `FileSystemWatcher` — changes take effect within seconds, no restart needed.

## Choosing an LLM

Any OpenAI-compatible endpoint works. Some options:

| Provider | Endpoint | Notes |
|----------|----------|-------|
| [OpenRouter](https://openrouter.ai/) | `https://openrouter.ai/api/v1` | Recommended — access to many models with one key |
| [OpenAI](https://platform.openai.com/) | `https://api.openai.com/v1` | GPT-4o, GPT-4.1, etc. |
| [Ollama](https://ollama.com/) (local) | `http://host.docker.internal:11434/v1` | Free, runs on your machine. Use `host.docker.internal` to reach the host from Docker |

To use Ollama locally, set in your `.env`:

```env
LLM_ENDPOINT=http://host.docker.internal:11434/v1
LLM_API_KEY=ollama
LLM_MODEL_ID=llama3.1
```

## Connecting MCP servers and A2A agents

The agent's MCP bridge and A2A client make standard HTTP calls — they work the same in Docker as in Kubernetes. Any SSE-based MCP server or A2A agent reachable over HTTP can be registered.

### MCP servers

Edit `/data/agent/mcp.json` on the volume to add SSE-based MCP servers:

```json
{
  "mcpServers": {
    "my-server": {
      "type": "sse",
      "url": "http://host.docker.internal:3000/"
    }
  }
}
```

Use `host.docker.internal` to reach servers running on your host machine. For servers running as additional Compose services, use the service name as the hostname (e.g., `http://my-mcp-service:8080/`).

The agent watches `mcp.json` with a `FileSystemWatcher` and **hot-reloads** when the file changes — no restart needed.

> **Note**: `stdio`-based MCP servers (the `"command"` transport) are not supported in this Docker setup — only HTTP/SSE endpoints.

### A2A agents

Edit `/data/agent/well-known-agents.json` on the volume **before starting the agent** to pre-register A2A agents:

```json
[
  {
    "agentName": "my-agent",
    "description": "What this agent does",
    "version": "1.0",
    "url": "http://host.docker.internal:5000",
    "skills": [
      {
        "id": "my-skill",
        "name": "My Skill",
        "description": "What this skill does"
      }
    ]
  }
]
```

This file is read once at startup, so changes require a restart (`docker compose restart agent`). To register agents at runtime without restarting, ask the agent to use its `register_agent` tool during a conversation.

## Stopping and cleaning up

```bash
# Stop all containers (data is preserved in volumes)
docker compose down

# Stop and remove all data (fresh start)
docker compose down -v
```

## Next steps

This minimal setup gets you chatting with the agent. For the full experience with script execution, MCP tools, and multi-agent coordination, see the [Helm deployment guide](../deploy/values.personal.example.yaml) for Kubernetes.
