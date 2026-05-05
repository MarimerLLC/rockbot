# MCP Attachment Passthrough

## Why

The model talks to MCP tools like `send_email` and `get_email_attachment`. These tools take or
return files. There are three ways to move bytes between the model and the tool, and only one
of them is reasonable:

1. **Base64 in the LLM context** — burns tokens, bloats prompts, and doesn't scale to anything
   bigger than a thumbnail. The model also routinely truncates or hallucinates long base64
   strings.
2. **REST stash + handle** — the tool exposes `POST /attachments` (upload), `GET
   /attachments/{id}` (download), and `DELETE /attachments/{id}` (cleanup). The model has to
   orchestrate three calls and track an opaque `attachmentId`. Models do this badly.
3. **Filesystem path** — both the agent pod and the script pod mount the same shared volume.
   The model just says "the file is at `/rockbot/shared/attachments/foo.pdf`" and something
   else handles the byte movement.

(3) is the only option that keeps tokens out of context AND doesn't require the model to be a
good orchestrator. The bridge sits between the model and the MCP server and translates between
(3) and whatever the tool actually understands.

## Storage location

Files live under `${ROCKBOT_SHARED_PATH}/attachments/` (defaults to
`/rockbot/shared/attachments/` when the env var is unset). This is the **shared PVC**, not the
agent PVC at `/data/agent/` — script pods only mount the shared volume, and the
"generate-then-attach" flow requires both processes to see the same bytes.

The directory grows monotonically until the operator cleans it. TTL-based cleanup is a
follow-up; the issue's "start simple" guidance applies.

## Architecture

The transform is a JSON pre/post pass at `McpBridgeService.HandleToolInvokeAsync` — the single
place every MCP tool call goes through, regardless of whether it arrived via direct
`tool.invoke.mcp` or the `mcp_invoke_tool` indirection. Hooking it there guarantees consistent
behavior.

Per-server opt-in via the `attachments` block in `mcp.json`. No manifest → the gateway is a
no-op for that server.

```
Agent: send_email({ attachments: [{ path: "/rockbot/shared/attachments/x.pdf" }] })
    │
    ▼
McpBridgeService.HandleToolInvokeAsync
    │   AttachmentGateway.RewriteRequestAsync
    │     bytes < threshold → { name, base64Content }
    │     bytes ≥ threshold → POST /attachments → { attachmentId }
    │
    ▼
McpClient.CallToolAsync(send_email, rewritten args)
    │
    ▼
ToolInvokeResponse (unchanged for outbound)
```

```
Agent: get_email_attachment({ attachmentId: "...", mode: "save" })
    │
    ▼
AttachmentGateway.RewriteRequestAsync
    │   mode: "save" → mode: "stash"  (or "inline" when sizeHint < threshold)
    │
    ▼
McpClient.CallToolAsync(get_email_attachment, rewritten args)
    │   returns { attachmentId, name } (stash) or { name, base64Content } (inline)
    │
    ▼
AttachmentGateway.RewriteResponseAsync
    │   stash: GET /attachments/{id} → write to disk → DELETE /attachments/{id}
    │   inline: base64-decode → write to disk
    │
    ▼
ToolInvokeResponse: { path, name, size, mime }
```

## Routing rules

- **Outbound below threshold** → inline `base64Content`.
- **Outbound at/above threshold** → multipart `POST /attachments`. Accept **both 200 and 201**
  (some servers return 200 for create, others 201).
- **Inbound `mode: "save"`** → stash by default; inline only when `sizeHint < threshold`.
  Either way the response is rewritten to `{path, name, size, mime}`.
- **Inbound `mode: "stash"`** (model passed it explicitly) → passthrough. The model wants the
  raw handle.
- **DELETE** is fire-and-forget, idempotent, **404-tolerated**.

## Key design decisions

### Storage on the shared PVC, not the agent PVC

The issue specifies `/data/agent/attachments/`, but `/data/agent` is the agent's private PVC.
Script pods don't mount it. The "generate a spreadsheet in a script and attach it to email"
flow only works if both processes see the same bytes — so the gateway uses
`${ROCKBOT_SHARED_PATH}/attachments/` instead. The same env var is exported to script pods, so
no per-environment configuration is needed.

### Per-server manifest, not schema-derived

MCP doesn't (yet) standardize an attachment-shape annotation. We could heuristically detect
"a parameter named `attachments`" and "a tool whose response has `base64Content`," but that
gets fragile fast. The explicit per-server manifest in `mcp.json` is the "start here" choice;
schema annotations or convention-by-name are follow-ups.

### Single `paramPaths` shape (`arrayKey[*]`)

Real MCP servers we've seen follow this exact pattern: a top-level array of attachment objects
each with a `path` (or `base64Content`) field. Deeper JSON Pointer support
(`a.b.c[*].d`) can be added when a real server needs it.

### No single-hop REST inbound

The first instinct was to route inbound entirely through REST (`GET /attachments/{id}`) and
skip the MCP call. That doesn't work for the providers we care about — Gmail/Outlook
attachment IDs are scoped by `(accountId, emailId)`, and the REST endpoint can't recover that
context from an opaque ID alone. So inbound goes: MCP `mode: "stash"` → server returns ID →
gateway GETs → gateway DELETEs.

### Inbound shape hard-coded to `{path, name, size, mime}`

Configurable per-server response shapes are a small follow-up if a server insists on a
different envelope. v1 keeps it simple.

## Verified calendar-mcp REST shape

- `POST /attachments` (multipart, field `file` by default) → **201 Created** with
  `{ "attachmentId": "..." }`. Some forks return 200 — we accept both.
- `GET /attachments/{id}` → **200 OK**, body is the raw bytes,
  `Content-Disposition: attachment; filename="..."` carries the original name,
  `Content-Type` carries the MIME.
- `DELETE /attachments/{id}` → **204 No Content** (or 404 if already gone — tolerated).

These shapes informed the gateway's verifier (status code acceptance, header parsing).

## What does NOT happen

- **Streaming / chunked transfer.** Attachments fit in memory. If we need streaming, that's a
  v2.
- **Encryption at rest.** The shared volume is the trust boundary; the model already lives
  inside it.
- **A2A / Web subsystems.** Neither has a binary-content story today; out of scope.
- **TTL cleanup.** Follow-up `IHostedService` if the directory growth becomes a problem.
- **Activity-log surfacing.** The bridge logger emits structured events; surfacing those to
  the agent activity log is a v2 enhancement.

See [`mcp-bridge.md`](mcp-bridge.md) for the broader bridge architecture, and
[`docs/tools.md`](../docs/tools.md#attachment-passthrough) for the operator-facing manifest
reference.
