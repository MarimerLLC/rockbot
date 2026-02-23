# ClaudeCodeProxy

An OpenAI-compatible `/v1/chat/completions` proxy for the Anthropic Messages API.
Lets RockBot (or any OpenAI SDK client) use Claude models — including via a
**Claude Code Max subscription** — with zero changes to the calling code.

---

## Important: Personal Use and Terms of Service

> **This component is provided as-is for informational and personal-use purposes.**
> **Marimer LLC makes no representations about its compliance with Anthropic's**
> **Terms of Service for any particular use case. Responsibility for ensuring**
> **your use complies with Anthropic's terms rests entirely with you.**

### What Anthropic has said

In February 2026, Anthropic clarified their position on using Claude Code Max
subscriptions with the Agent SDK and personal automation projects:

- **Thariq Shihipar (Claude Code team):** *"Nothing is changing about how you
  can use the Agent SDK and MAX subscriptions. We want to encourage local
  development and experimentation."*
  — [The New Stack coverage of the clarification](https://thenewstack.io/anthropic-agent-sdk-confusion/)

- **Anthropic's stated distinction:** *"Personal/local use with a Max plan is
  fine; if you're building a business on top of the Agent SDK, use an API key."*
  — [The Register: Anthropic clarifies ban on third-party tool access to Claude](https://www.theregister.com/2026/02/20/anthropic_clarifies_ban_third_party_claude_access/)

- **Claude Code legal and compliance docs:**
  [https://code.claude.com/docs/en/legal-and-compliance](https://code.claude.com/docs/en/legal-and-compliance)

### What this means in practice

Based on Anthropic's clarification, using this proxy for a **personal, private
autonomous agent** (such as a self-hosted RockBot instance) appears to be
permitted. What is **not** permitted is using a Max subscription to build a
commercial product or service that you sell or provide to others.

**This is your call to make.** Anthropic's policies can change. Marimer LLC:

- Does **not** warrant that this technique is or will remain permitted
- Does **not** accept liability for any account suspension, service disruption,
  or other consequence arising from your use of this component
- Does **not** use this component in any commercial offering
- Provides this code solely as open-source reference material

If compliance is a concern, use the standard Anthropic Console API key path
(`ANTHROPIC_API_KEY`) instead — it has no ToS ambiguity.

---

## Authentication

Priority order:

| Priority | Method | How |
|----------|--------|-----|
| 1 | Console API key | `ANTHROPIC_API_KEY=sk-ant-...` |
| 2 | Explicit OAuth token | `CLAUDE_ACCESS_TOKEN=<token>` |
| 3 | Claude Code credentials file | `claude auth login` then mount `~/.claude/` |

For Max subscription use, run `claude auth login` once on your machine and mount
`~/.claude/` into the container at `/root/.claude` via a writable PVC (see below).

## Local Development

```bash
npm install
# Option A — use your API key
ANTHROPIC_API_KEY=sk-ant-... npm run dev

# Option B — use Max subscription credentials (already logged in)
npm run dev   # reads ~/.claude/.credentials.json automatically
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

### Option A — Console API key (no ToS ambiguity, recommended)

```yaml
# Helm values
secrets:
  llm:
    endpoint: "http://claude-code-proxy.rockbot.svc.cluster.local/v1"
    apiKey: "sk-ant-..."
    modelId: "claude-sonnet-4-6"
```

### Option B — Max subscription OAuth (personal use, see disclaimer above)

The credentials directory must be a **writable PVC** — Kubernetes Secret volumes
are read-only and the CLI cannot write renewed tokens back to them.

```bash
# Create the PVC
kubectl apply -f src/ClaudeCodeProxy/k8s/pvc.yaml

# Seed it from your local credentials (run once)
kubectl run claude-cred-seed --image=busybox --restart=Never -n rockbot \
  --overrides='{"spec":{"volumes":[{"name":"c","persistentVolumeClaim":{"claimName":"claude-credentials"}}],"containers":[{"name":"s","image":"busybox","command":["sh","-c","sleep 3600"],"volumeMounts":[{"name":"c","mountPath":"/creds"}]}]}}'
kubectl cp ~/.claude/.credentials.json rockbot/claude-cred-seed:/creds/.credentials.json
kubectl delete pod claude-cred-seed -n rockbot
```

Then deploy:
```bash
kubectl apply -f src/ClaudeCodeProxy/k8s/deployment.yaml
```

Point RockBot at the proxy:
```yaml
secrets:
  llm:
    endpoint: "http://claude-code-proxy.rockbot.svc.cluster.local/v1"
    apiKey: "unused"
    modelId: "claude-sonnet-4-6"
```

## Token Refresh

The proxy installs the `claude` CLI and runs `claude --version` at startup and
every 23 hours. The CLI uses the stored refresh token to obtain a new access
token and writes it back to `~/.claude/.credentials.json`. Since `auth.ts` reads
the file fresh on every request, renewed tokens are picked up automatically with
no restart needed.

The scheduler is skipped when `ANTHROPIC_API_KEY` or `CLAUDE_ACCESS_TOKEN` is
set — API key auth needs no refresh.

## Supported Endpoints

| Endpoint | Status |
|----------|--------|
| `GET /health` | ✅ |
| `GET /v1/models` | ✅ (static list) |
| `POST /v1/chat/completions` | ✅ non-streaming, full tool calling |
| `POST /v1/chat/completions` streaming | ❌ not yet implemented |

## Known Limitations

- **No streaming** — RockBot currently uses non-streaming completions, so this is fine
- **Model name passthrough** — the `model` field is sent as-is to Anthropic; use
  valid Anthropic model IDs (`claude-sonnet-4-6`, `claude-opus-4-6`, etc.)
