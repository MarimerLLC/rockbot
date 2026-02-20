# McpServer.OpenRouter

A standalone [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that exposes **read-only** OpenRouter account and usage information to AI agents.

This server is designed to run as a Kubernetes pod (or Docker container) inside the `rockbot` namespace, making OpenRouter data available to the RockBot agent without ever exposing the API key to the agent itself.

## Features

The server exposes these MCP tools:

| Tool | Description |
|------|-------------|
| `get_credits` | Current credit balance for the account |
| `get_api_key_info` | Details about the current API key (rate limits, usage) |
| `list_models` | All available models with context lengths and pricing |
| `list_api_keys` | All provisioned API keys for the organisation (management key required) |
| `get_api_key` | Details for a specific API key by hash (management key required) |
| `get_generation` | Details for a specific generation by ID (token usage, cost) |

> **Note:** No tools for purchasing credits or spending money are included.

## Configuration

The only required configuration is the OpenRouter API key. **Never put the key in `appsettings.json` or any committed file.**

| Setting | Environment variable | Description |
|---------|---------------------|-------------|
| `OpenRouter__ApiKey` | `OpenRouter__ApiKey` | OpenRouter management API key |
| `OpenRouter__BaseUrl` | `OpenRouter__BaseUrl` | API base URL (default: `https://openrouter.ai/api/v1`) |

### Local development

```bash
cd src/McpServer.OpenRouter
dotnet user-secrets set "OpenRouter:ApiKey" "sk-or-..."
dotnet run
```

The server starts on `http://localhost:5000` (or the port shown in the console). The MCP endpoint is at `/mcp`.

## Docker

Build the image from the repository root:

```bash
docker build -f src/McpServer.OpenRouter/Dockerfile -t mcpserver-openrouter .
```

Run with the API key injected via environment variable:

```bash
docker run -p 8080:8080 \
  -e OpenRouter__ApiKey="sk-or-..." \
  mcpserver-openrouter
```

## Kubernetes

The server is included in the RockBot Helm chart and disabled by default.

Enable it in your `values.personal.yaml`:

```yaml
openrouterMcp:
  enabled: true
  image:
    repository: <your-registry>/mcpserver-openrouter
    tag: latest

secrets:
  openRouter:
    apiKey: "sk-or-..."
```

Then upgrade the release:

```bash
helm upgrade --install rockbot deploy/helm/rockbot \
  -f deploy/values.personal.yaml \
  --create-namespace
```

The service is exposed internally as `rockbot-openrouter-mcp.rockbot.svc.cluster.local:80/mcp` (ClusterIP only — not exposed outside the cluster). Configure the agent's `mcp.json` to point to this URL.

## MCP endpoint

```
http://<host>:<port>/mcp
```
