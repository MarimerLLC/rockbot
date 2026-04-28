---
title: Getting started with Docker Desktop
nav_order: 2
---

# Getting Started with Docker Desktop

Run RockBot locally with Docker Compose on Docker Desktop. This is the simplest way to experiment with the agent — chat with it, explore its memory and skills system, and get a feel for how it works.

This minimal setup runs three containers: **RabbitMQ** (message bus), the **RockBot Agent** (the brain), and the **Blazor UI** (the chat interface). No Kubernetes, no Helm, no MCP servers.

> **What you can do**: Chat, build long-term memory, learn skills, web search, use the dream consolidation service. MCP and A2A also work if you register HTTP endpoints (see [Connecting MCP servers and A2A agents](#connecting-mcp-servers-and-a2a-agents) below).
>
> **What you can't do** (without Kubernetes): Run sandboxed Python scripts (requires the scripts-manager pod with K8s API access).

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) installed and running
- An LLM provider — either:
  - An **OpenAI-compatible LLM API key** — [OpenRouter](https://openrouter.ai/) is the easiest option, or
  - A **GitHub Copilot license** — uses per-request billing via the Copilot SDK (requires a GitHub token with `copilot` scope)
- A **Brave Search API key** (free tier available) — [https://api.search.brave.com/](https://api.search.brave.com/)

## 1. Set up the Docker Compose files

The compose template and example `.env` file live in the repo at [`deploy/docker-compose/`](../deploy/docker-compose/):

```
deploy/docker-compose/
  docker-compose.yml   # RabbitMQ + Agent + Blazor UI
  .env.example         # Template for your API keys
```

Copy them to a working directory (or run directly from the repo):

```bash
cp deploy/docker-compose/.env.example deploy/docker-compose/.env
```

Edit the `.env` file. Choose one of two LLM provider options:

**Option A — OpenAI-compatible (per-token billing):**

```env
LLM_API_KEY=sk-or-v1-your-openrouter-key-here
LLM_ENDPOINT=https://openrouter.ai/api/v1
LLM_MODEL_ID=anthropic/claude-haiku-4.5
BRAVE_API_KEY=BSA-your-brave-key-here
```

**Option B — GitHub Copilot SDK (per-request billing):**

```env
LLM_PROVIDER=Copilot
LLM_MODEL_ID=gpt-4.1
GITHUB_TOKEN=ghp_your-token-with-copilot-scope
BRAVE_API_KEY=BSA-your-brave-key-here
```

> **Do not commit the `.env` file to version control.** It contains your API keys.

## 2. Start everything

```bash
cd deploy/docker-compose
docker compose up -d
```

Wait about 30 seconds for RabbitMQ to become healthy and the agent to connect. You can follow the agent logs to see when it's ready:

```bash
docker compose logs -f agent
```

Look for log output indicating the agent has connected to RabbitMQ and is listening for messages.

## 3. Chat with the agent

Open your browser to **[http://localhost:8080](http://localhost:8080)** to access the Blazor chat UI.

Start a conversation — the agent will respond using your configured LLM. Try things like:

- Ask it a question (it will use web search if needed)
- Ask it to remember something ("remember that I prefer dark mode")
- Ask it to recall something ("what do you know about me?")
- Ask it about its skills or capabilities

## 4. Monitor and debug

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

Once the stack is running, the next step is onboarding the agent so it has the right identity, memory behavior, MCP integrations, and scheduled jobs. See [Getting started with RockBot](getting-started-rockbot).

## Choosing an LLM

### OpenAI-compatible providers (per-token billing)

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

### GitHub Copilot SDK (per-request billing)

If you have a GitHub Copilot license, you can use it as the LLM provider. The Copilot SDK bundles its own CLI binary — no separate installation needed. Models available include GPT-4.1, Claude Sonnet 4, and others depending on your Copilot subscription tier.

```env
LLM_PROVIDER=Copilot
LLM_MODEL_ID=gpt-4.1
GITHUB_TOKEN=ghp_your-token-with-copilot-scope
```

Create a token at [github.com/settings/tokens](https://github.com/settings/tokens) with the `copilot` scope.

### Mixing providers across tiers

Each tier (Low/Balanced/High) can use a different provider. Set a per-tier `Provider` override in your Helm values or environment variables. For example, Copilot for Low, OpenRouter for Balanced and High. See `deploy/values.personal.example.yaml` for examples.

## Enabling hybrid vector search (optional)

By default the agent uses BM25 keyword search for memory, skills, and working memory recall. You can optionally add a text embedding model to enable **hybrid search** (BM25 + cosine similarity), which improves recall for semantically similar but lexically different content.

Any OpenAI-compatible embedding endpoint works. The easiest local option is [Ollama](https://ollama.com/):

1. Install and start Ollama on your host machine
2. Pull an embedding model: `ollama pull nomic-embed-text`
3. Uncomment the embedding lines in your `docker-compose.yml` agent environment and in your `.env`:

```env
EMBEDDING_ENDPOINT=http://host.docker.internal:11434
EMBEDDING_MODEL=nomic-embed-text
```

4. Restart the agent: `docker compose restart agent`

The agent logs will confirm: `Embedding model configured: nomic-embed-text @ http://host.docker.internal:11434`. If the embedding config is missing or the endpoint is unreachable, the agent falls back to BM25-only search with no loss of functionality.

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

This minimal setup gets you chatting with the agent. Next, follow [Getting started with RockBot](getting-started-rockbot) to turn that running agent into a useful personal one. For the full experience with script execution, MCP tools, and multi-agent coordination, see the [Helm deployment guide](../deploy/values.personal.example.yaml) for Kubernetes.
