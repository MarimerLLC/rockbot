# MCP Elicitation

How the bridge answers a question an MCP server asks in the middle of a tool call.

Operator-facing reference: `docs/tools.md` → "Elicitation". This document records the design
decisions that must survive future refactors.

## What the protocol does

Since the 2025-06-18 revision, MCP is no longer strictly request/response from client to
server. A server handling `tools/call` may turn around and send the *client* an
`elicitation/create` request — "which mailbox?", "this will overwrite 12 rows, confirm?" —
and block its own tool call until the client answers. The client answers with one of three
actions:

| Action | Meaning |
|---|---|
| `accept` | The form was filled in; `content` carries the values. |
| `decline` | The client explicitly refuses. The server continues without the value. |
| `cancel` | Dismissed without a choice. The protocol default. |

There are two modes. **form** mode carries a flat JSON-Schema subset (string, number/integer,
boolean, single- and multi-select enums) that the client fills in. **url** mode hands the
client a URL and expects it to walk a person through a browser flow out of band.

A client that does not advertise the `elicitation` capability should never be asked. A client
that advertises it and then hangs turns every such tool call into a timeout.

## Why this needed a design and not a default

The obvious "safe" answer — never advertise the capability — is not safe, it is just quiet.
Servers that use elicitation do so because the call genuinely cannot proceed without the
answer, so the tool fails, and the agent has no idea why.

The next-most-obvious answer — decline everything and tell the agent to retry with the value —
does not terminate. The elicited field is frequently *not* a tool parameter ("which of these
three matches did you mean?"), so the retried call asks the identical question and the agent
loops until its iteration budget runs out.

So the answer has to be produced in-band, inside the caller's tool-call timeout. Everything
below follows from that.

## Decisions and rationale

### The bridge answers, under an explicit per-server policy

Elicitation is the one place where an MCP server drives the conversation, so it gets a policy
object (`McpElicitationConfig`, per server in `mcp.json`, with a bridge-wide
`McpBridge:DefaultElicitation` fallback) rather than an implicit "whatever the model says".

| Mode | Behaviour |
|---|---|
| `auto` (default) | Answer form-mode questions from configured defaults, then the responder. |
| `decline` | Advertise the capability, answer `decline` every time. |
| `off` | Do not advertise the capability at all. |

An unrecognized mode resolves to `decline`. A typo in a policy file must never widen what the
bridge is willing to answer.

### `off` means "no handler", not "handler that says no"

The C# SDK advertises `elicitation` during `initialize` exactly when
`McpClientHandlers.ElicitationHandler` is set. `McpElicitationCoordinator.TryCreate` returns
`null` for `off`, the bridge leaves `McpClientOptions.Handlers` unset, and a well-behaved
server never asks. That is a cleaner contract than advertising support and refusing every
question — the server can pick a different code path at negotiation time.

### Only `form` mode is supported; `url` mode is declined

url mode exists so sensitive flows (OAuth, payment, credential entry) happen in a browser the
client never sees. A headless agent has no browser and no person attached to the call. The SDK
advertises form support only, so a url request is a server ignoring our declared capabilities;
it gets `decline` with that reason.

### Every answer is validated against the server's own schema

`McpElicitationSchemaValidator` is the trust boundary. Whatever produced a value — a model, a
configured default, some future human-in-the-loop responder — only values that fit the schema
the server sent are forwarded. Nothing is coerced: a string where a number was asked for is an
error, not a parse, because a server that receives a plausible-looking wrong type acts on it.

Invented fields are dropped rather than rejected (they are noise, not a lie). Omitted optional
fields are left out entirely so the SDK's `ElicitResult.WithDefaults` can still apply the
schema's own defaults.

### Credential-shaped fields decline the whole request, before the responder runs

The spec says servers MUST NOT elicit credentials. A client that relies on servers behaving is
not a security boundary. `McpSensitiveFieldDetector` matches the field name, title, and
description against credential vocabulary; any hit declines the *entire* request rather than
answering the other fields, because a partial answer tells the server which fields the bridge
is willing to fill.

This also protects the responder from itself: secrets are never in LLM context by design
(`design/security.md` → Secrets Management), so any value a model produced for such a field
would be a hallucination or a leak.

### A form of nothing but booleans is a decision, not a question

A confirmation prompt ("this overwrites 12 rows, continue?") is a second checkpoint the server
put there for a person, *after* the agent already decided to make the call. Answering it from
the model makes the checkpoint decorative. Any form whose every field is a `BooleanSchema` is
declined — unless the operator pre-answered it via `defaults`, which is an explicit, auditable
decision made outside the model's reach.

### The responder is an interface, and it is not a second agent

`IMcpElicitationResponder` exists so the decision of *who answers* is a deployment choice. The
shipped `LlmElicitationResponder` runs in the bridge with no conversation, no memory, and no
tools, and is prompted to answer only from the in-flight tool call's own arguments — a
transcription step, not an agent. A question it cannot answer from the call is declined and
handed back to the agent, which *does* have the conversation and can ask the person.

A deployment that can reach a person inside a tool-call timeout registers a different
responder. Nothing else changes.

### Who answers is chosen per server

Servers ask different kinds of question. A mail server's "which mailbox?" is transcription from
the call's own arguments; a research server's "which of these meanings did you intend?" needs
the conversation. So besides the host's default `IMcpElicitationResponder`, a server's policy
can name a responder (`responder`), resolved from the host's keyed services
(`McpElicitationResponders.Resolve`). The shipped one is registered under
`LlmElicitationResponder.Key` (`"llm"`).

A name with no registered responder leaves the server with **no** responder — only configured
`defaults` are answered — rather than falling back to the default. Same rule as an unknown
mode: a typo narrows what the bridge answers, never widens it. And since `responder` lives in
the `elicitation` block, a model-registered server cannot pick its own.

Each in-flight call carries the agent session that made it (`ToolInvokeRequest.SessionId`,
forwarded through `mcp_invoke_tool` and recovery retries), and the responder sees it on
`McpElicitationCallContext.SessionId`. The shipped responder ignores it; a responder that puts
the question back to the agent needs it to know which conversation to ask in.

The responder's LLM call goes through `ILlmClient` at `ModelTier.Low` and is bounded by
`ResponderTimeoutMs` (default 20 s) inside the caller's tool-call budget: better a declined
elicitation than a timed-out tool call.

### The agent is told what was asked

Records of each round are attached to the tool result as an extra text block
(`McpElicitationNote`). Without it the agent sees a thin or empty result, has no idea a
question was asked and declined, and retries the identical call. The note names the missing
fields so the next attempt can carry them as ordinary tool arguments, or the agent can put the
question to the user. A timeout error gets the same treatment, since a call that times out
just after a declined elicitation almost certainly timed out *because* of it.

"Retry with the value" is only advice when the value has somewhere to go. The bridge checks each
declined field against the called tool's discovered input schema: a field that is a parameter
gets "supply it and call again"; one that is not ("which of these matches did you mean?") gets
"this is not a parameter of this tool, so calling again will only get the same question — ask
the user". Otherwise the agent adds an argument the server ignores, is asked again, and loops
until its iteration budget runs out. When the schema is unknown — including `invoke_tool`
dispatchers, whose real parameters belong to the inner tool — the note keeps the general
wording.

Server-authored text in the note is flattened to a single line. It is untrusted content
rendered inside a tool-result block; multi-line text would let it forge its own bullet lines.

### Attribution is approximate, and says so

MCP carries no link from an `elicitation/create` request back to the request that provoked it,
and the SDK dispatches server-initiated requests on the session's own message loop rather than
on the caller's async context — so neither the request id nor an `AsyncLocal` ties the two
together. The bridge tracks open calls per server (`McpElicitationCallScope`): exact while one
call is open, and explicit about the ambiguity when several are, in which case the record is
attached to all of them and the responder is told.

An elicitation arriving with *nothing* in flight — during discovery, say — is declined. There
is no caller waiting on it and no tool call to answer from.

### Every path terminates, and the handler never throws

`MaxPerCall` (default 3) caps rounds per tool call; past it every request is declined. A server
that dislikes an answer and keeps re-asking would otherwise spin the bridge, and the model
behind it, until the tool-call timeout. Responder exceptions, responder timeouts, unreadable
answers and validation failures all resolve to `decline` — a thrown handler leaves the server
holding a protocol error where the spec gives it a defined outcome.

### LLM-registered servers cannot write their own policy

`mcp.json` is LLM-writable via `register_mcp_server`, but `McpRegisterServerRequest` carries no
`elicitation` field, so a server the model registers at runtime always gets
`DefaultElicitation`. It cannot ship its own `defaults` or relax `deniedFields`. The credential
heuristics are code, not config, and cannot be turned off from a config file at all.

## Configuration

```json
{
  "mcpServers": {
    "mail": {
      "type": "streamable-http",
      "url": "http://mcp-mail:8080/mcp",
      "elicitation": {
        "mode": "auto",
        "maxPerCall": 3,
        "responderTimeoutMs": 20000,
        "responder": "llm",
        "defaults": { "mailbox": "work" },
        "deniedFields": ["accountId"]
      }
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `mode` | `auto` | `auto`, `decline`, or `off`. Anything else is read as `decline`. |
| `maxPerCall` | 3 | Rounds answered per tool call. `0` answers none. |
| `defaults` | `{}` | Deterministic answers by field name (case-insensitive), applied before the responder and overriding it. Still schema-validated, so a stale default is dropped rather than forwarded. |
| `deniedFields` | `[]` | Extra field names to refuse, on top of the built-in credential heuristics. |
| `responderTimeoutMs` | 20000 | Budget for the responder, inside the tool-call timeout. |
| `responder` | *(host default)* | Keyed-service name of the responder for this server. Unregistered names answer from `defaults` only. |

Omit the block entirely to inherit `McpBridge:DefaultElicitation`.

## Code map

| Type | Role |
|---|---|
| `McpElicitationConfig` | Per-server policy. |
| `McpElicitationCoordinator` | Enforces the policy; one per connected server; supplies the SDK's elicitation handler. |
| `McpElicitationCallScope` | Marks a tool call in flight; collects what was asked. |
| `IMcpElicitationResponder` / `McpElicitationAnswer` | Who answers, and what they propose. |
| `LlmElicitationResponder` | The shipped responder: answers from the in-flight call's arguments. |
| `McpElicitationResponders` | Picks a server's responder: named (keyed service) or the host default. |
| `McpElicitationSchemaValidator` | The trust boundary. |
| `McpElicitationSchemaDescriber` | Renders a schema for prompts and notes. |
| `McpSensitiveFieldDetector` | Credential vocabulary. |
| `McpElicitationNote` | Tells the agent what happened. |

## Not done

- **Sampling.** `sampling/createMessage` is the other server-to-client request — a server
  asking the client to run an LLM turn on its behalf. No handler is registered, so the
  capability is not advertised and compliant servers will not ask. It needs its own policy
  (which tier, whose budget, what the server is allowed to see) and is deliberately out of
  scope here.
- **url-mode elicitation.** Requires a user agent and a person; see above.
- **Task-augmented elicitation.** `McpClientOptions.TaskStore` is unset, so the client does not
  accept task-augmented elicitation requests. The synchronous path covers the servers we run.
