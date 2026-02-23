# ClaudeCodeProxy

An OpenAI-compatible `/v1/chat/completions` proxy for the Anthropic Messages API.
Lets RockBot (or any OpenAI SDK client) use Claude models — including via a
**Claude Code Max subscription** — with zero changes to the calling code.

## Authentication

Priority order:

| Priority | Method | How |
|----------|--------|-----|
| 1 | Console API key | `ANTHROPIC_API_KEY=sk-ant-...` |
| 2 | Explicit OAuth token | `CLAUDE_ACCESS_TOKEN=<token>` |
| 3 | Claude Code credentials file | `claude auth login` then mount `~/.claude/` |

For Max subscription use, run `claude auth login` once on your machine and either:
- Mount `~/.claude/` into the container at `/root/.claude`, or
- Create a Kubernetes secret from the credentials file (see below)

Per Anthropic's February 2026 clarification, personal private projects may use
Claude Code Max OAuth tokens directly with the Anthropic Messages API.

## Local Development

```bash
npm install
# Option A — use your API key
ANTHROPIC_API_KEY=sk-ant-... npm run dev

# Option B — use Max subscription credentials (already logged in)
npm run dev   # reads ~/.claude/credentials.json automatically
```

Test with curl:
```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "claude-sonnet-4-6",
    "messages": [{"role": "user", "content": "Say hello"}]
  }'
```

## Docker

```bash
# Build
docker build -t rockylhotka/claude-code-proxy:latest .

# Run with API key
docker run -p 8080:8080 -e ANTHROPIC_API_KEY=sk-ant-... rockylhotka/claude-code-proxy

# Run with Max credentials
docker run -p 8080:8080 -v ~/.claude:/root/.claude rockylhotka/claude-code-proxy
```

## Kubernetes Deployment

### Option A — Console API key (Kubernetes secret)

Add to your Helm values:
```yaml
secrets:
  llm:
    endpoint: "http://claude-code-proxy.rockbot.svc.cluster.local/v1"
    apiKey: "sk-ant-..."          # Console API key
    modelId: "claude-sonnet-4-6"
```

### Option B — Max subscription OAuth (mount credentials)

Create a secret from your local Claude credentials:
```bash
kubectl create secret generic claude-credentials \
  -n rockbot \
  --from-file=credentials.json=$HOME/.claude/credentials.json
```

The proxy Helm chart (when added) mounts this secret at `/root/.claude/credentials.json`.

## Supported Endpoints

| Endpoint | Status |
|----------|--------|
| `GET /health` | ✅ |
| `GET /v1/models` | ✅ (static list) |
| `POST /v1/chat/completions` | ✅ non-streaming, full tool calling |
| `POST /v1/chat/completions` streaming | ❌ not yet implemented |

## RockBot Integration

Point RockBot at this proxy by changing `LLM__Endpoint` and `LLM__ModelId`:

```yaml
# In your Helm values / environment
LLM__Endpoint: "http://claude-code-proxy.rockbot.svc.cluster.local/v1"
LLM__ApiKey: "ignored"            # proxy handles auth; any non-empty value works
LLM__ModelId: "claude-sonnet-4-6"
```

No code changes to RockBot required — it already uses the OpenAI SDK with a
configurable endpoint.

## Known Limitations

- **No streaming** — RockBot currently uses non-streaming completions, so this is fine
- **OAuth token expiry** — Max credentials expire and need re-login; the proxy reads
  the credentials file on each request so a background token refresh daemon will
  keep it current without restarts
- **Model name passthrough** — request `model` field is sent as-is to Anthropic;
  use valid Anthropic model IDs (`claude-sonnet-4-6`, `claude-opus-4-6`, etc.)
