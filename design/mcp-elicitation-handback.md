# MCP Elicitation Hand-back

**Status:** Proposed. Remaining part of #602; builds on MCP elicitation (#593), the
`conversation` responder (#605) and MCP C# SDK 2.x (#607).

## Problem

Today a server's mid-call question is answered **in-band**, inside the tool call, by a responder
that runs beside the calling agent:

- `llm` answers from the call's own arguments.
- `conversation` answers choice fields from the recent conversation.

Neither can do what the question often needs: let the **calling agent** answer from everything
it knows, or **ask the user** and wait for the reply. Anything they can't settle is declined, and
the agent learns about it only from the note on a failed or partial result. It then has to call
the tool again from scratch, if the answer has anywhere to go on a retry at all.

AdvisorCouncil makes this concrete (`design/advisor-council-mcp.md`). Risk appetite and
priorities usually aren't in the conversation. Every such question costs a round trip: the
council declines or returns early, the agent asks the user, then calls the council again.

## Goal

Hand the question to the calling agent's own loop. The tool call returns at once with the
question. The agent answers it, from its context or by asking the user, possibly many turns
later. The original call then resumes where it stopped.

## What the SDK allows: park, don't capture

Resuming later needs the server's `InputRequiredResult` (its questions plus `requestState`),
which the bridge could store and re-issue with `inputResponses`. **MCP C# SDK 2.2.0 doesn't
expose it.** It resolves MRTR inside the client's request pipeline on every path. A probe
against a real 2.2.0 server showed that `CallToolAsync`, `CallToolAsync(CallToolRequestParams)`
and the raw `McpSession.SendRequestAsync(JsonRpcRequest)` all call `ElicitationHandler` and
return the *resumed* result. None of them returns the `InputRequiredResult`.

So the bridge **parks** the call instead:

- The original `CallToolAsync` keeps running in the background.
- The elicitation handler awaits the agent's answer rather than producing one.
- The bridge replies to the agent with the question.
- When the agent answers, the handler returns that answer, and the SDK retries the call as
  usual.

Under MRTR this is cheap, because **the server holds nothing while the question waits**: it
returned `InputRequiredResult` and forgot the call. Only the bridge holds a pending task.

Consequence: the parked call lives in the bridge's memory. A restart of the agent process (the
bridge runs inside it) loses it. What does *not* get lost is the knowledge that it was pending.
Every hand-back is written to a durable **pending ledger** before the agent sees the question,
with enough context about the original call for a freshly restarted agent to understand what
failed. On startup the bridge turns every still-pending entry into an **interruption notice** to
its session. (See "Pending ledger and restart recovery".) If a later SDK exposes the
`InputRequiredResult` (worth asking upstream), the ledger could also hold the server's
`requestState`, and an interrupted call could be resumed rather than redone.

## Flow

```
agent ──tool call──► bridge: start CallToolAsync in the background (parked task T)
                        │
       server ◄─────────┘  tools/call
       server ──InputRequiredResult──► SDK ──ElicitationHandler──► coordinator
                                                                     │ hand-back mode
                                                                     ▼
                                          PendingQuestion { id, session, question, TCS }
                        ┌── signal ─────────────────────────────────┘
agent ◄──tool result── bridge: "the server asks …; answer with mcp_answer(question_id, …)"
  │   (the agent answers now, or asks the user and ends its turn)
  │
agent ──mcp_answer(question_id, answers)──► bridge: validate → complete TCS
                                            SDK retries tools/call with inputResponses
       server ──result──► T completes ──► mcp_answer's result is the tool's result
                    (or another question → another hand-back, bounded by maxPerCall)
```

The invoke path waits for whichever comes first: T completing (no question, so a normal
result) or a hand-back signal (reply with the question). `mcp_answer` does the same after
completing the TCS, so a server that asks again produces another hand-back, not a hang.

## Policy

Hand-back is a new elicitation mode, `"mode": "handback"`, set in a **server's own policy**.
Like the `conversation` responder, it's refused in `McpBridge:DefaultElicitation`. The server's
question goes into the agent's context, and the agent's answer goes to the server, so it isn't
extended by default to servers the model registered itself.

Everything the coordinator already does still runs first, in-band:

- **Credential-shaped fields, denied fields, unreadable forms and url mode:** declined. The
  agent is never asked for a secret.
- **Configured `defaults` that settle the whole form:** answered, with no hand-back.
- **`maxPerCall`:** bounds the rounds, as before.

What reaches the agent is a question the operator wants the agent to own.

**Older servers.** Hand-back is only used when the server negotiated 2026-07-28 or later
(`McpProtocolVersions.IsJuly2026OrLaterProtocolVersion`). A legacy `elicitation/create` holds
the server's request open for as long as the question waits, so for those servers hand-back mode
behaves like `auto`, using the server's responder.

## What the agent sees

The original tool call returns a normal (non-error) result:

```
[rockbot] The MCP server "research" needs input before it can finish research(...):
  "Which Mercury do you mean?"
  Fields: meaning (one of: planet, element, band, required)
Answer with mcp_answer(question_id: "q_7f3c…", answers: {"meaning": "..."}), or
mcp_answer(question_id: "q_7f3c…", decline: true). If you don't know, ask the user first;
the question stays open for 30 minutes.
```

- **The question text is untrusted server content.** Field names, the message and option values
  are flattened, as in `McpElicitationNote`.
- **`question_id` is a random, unguessable id.** The server's `requestState` never reaches the
  model.
- **The same content goes out as a structured block**, so the UI can render it as a form.

## `mcp_answer`

A new management tool alongside `mcp_invoke_tool`:

| Argument | Type | |
|---|---|---|
| `question_id` | string, required | From the hand-back. |
| `answers` | object | Field values, keyed by the server's field names. |
| `decline` | bool | Refuse the question; the server continues without the value. |

The bridge checks the following, and any failure is a tool error the agent can act on, not a
decline sent to the server:

1. **The question exists, is still pending, and hasn't expired.** Otherwise the error says which:
   "expired", "already answered", or "interrupted by a restart". The last one carries the same
   call context as the interruption notice, from the ledger.
2. **Same session.** `ToolInvokeRequest.SessionId` of the `mcp_answer` call equals the session
   that made the original call. A question can't be answered from another conversation, a
   subagent or a scheduled task.
3. **Schema.** `McpElicitationSchemaValidator` against the server's own schema, with no
   coercion, as for every other answer.
4. **Decision fields need the user.** A boolean or yes/no choice (the coordinator's decision
   fields) is only accepted if a **user turn** was recorded in the session's conversation memory
   *after* the question was handed back. The agent can't confirm "overwrite 12 rows?" on its own;
   it has to put the question to the user, and the user has to answer. Other fields need no
   user turn: the agent may answer them from its context, just as it chooses tool arguments.
5. **One-shot.** A question is answered once. A second `mcp_answer` gets "already answered".

## Lifetime and limits

| Setting | Default | |
|---|---|---|
| `handbackTtlMinutes` (per server) | 30 | After this, the parked call is cancelled and the server receives `cancel`. A later `mcp_answer` gets "expired". |
| `McpBridge:MaxPendingQuestionsPerSession` | 5 | Past this, new questions for the session are declined in-band. |
| `McpBridge:MaxPendingQuestions` | 50 | Bridge-wide cap on parked calls. |

- **The parked call ignores the tool-call timeout.** `toolTimeoutMs` bounds each *active*
  stretch (before the question, and after the answer), not the time the question waits. The
  TTL bounds that.
- **Server-side cost varies with how the server asks.**
  - A server that throws `InputRequiredException` holds nothing.
  - A server that calls `ElicitAsync` inside its handler, under MRTR, keeps that handler
    suspended until the retry arrives. Its own limits apply, and a long wait may find the
    server has given up. Hand-back suits the first kind; `design/research-agent-mcp.md` and
    `design/advisor-council-mcp.md` both use `InputRequiredException`.

## Pending ledger and restart recovery

The parked call can't survive a restart, but the fact that it was waiting can, along with what
it was doing. A restart must never leave a question silently dangling. The rules:

- The bridge knows **deterministically** which calls a restart interrupted.
- The session that made each call is told **proactively**, without waiting for it to try
  `mcp_answer`.
- The notice carries enough **context about the original call** for a freshly restarted agent
  (no in-memory state, only its conversation) to decide whether to redo it, and how.

### What is recorded

One entry per handed-back question. It's written **before** the hand-back is returned to the
agent (write-ahead), so any question an agent has seen has a ledger entry. An example entry:

```json
{
  "questionId": "q_7f3c2a…",
  "status": "pending",
  "sessionId": "abc123",
  "createdAt": "2026-10-07T14:02:11Z",
  "expiresAt": "2026-10-07T14:32:11Z",
  "call": {
    "server": "research",
    "tool": "research",
    "toolDescription": "Research a topic using web search and page fetching, then synthesise a concise answer.",
    "arguments": "{\"question\":\"Tell me about Mercury\",\"context\":\"for a telescope night\"}",
    "startedAt": "2026-10-07T14:02:09Z",
    "round": 1,
    "earlierRounds": []
  },
  "triggeredBy": {
    "at": "2026-10-07T14:01:58Z",
    "userExcerpt": "I'm planning a telescope night next week and want to see Mercury — can you look into it?"
  },
  "question": {
    "message": "Which Mercury do you mean?",
    "fields": [
      { "name": "meaning", "type": "one of: planet, element, band", "required": true }
    ]
  },
  "notifiedAt": null,
  "resolvedAt": null
}
```

Why each part is there:

| Part | Tells the restarted agent |
|---|---|
| `call.server`, `call.tool`, `call.toolDescription` | *What* was being done. The description is the tool's own, from `tools/list`, truncated to 300 characters, because the restarted agent may not have that tool's schema in context. |
| `call.arguments` | *How* to redo it: the arguments the agent sent. |
| `triggeredBy.userExcerpt` | *Why* it was done: the last user message in the session when the call started. That's the request the call was serving. |
| `question` | *What the server still needed* when the call stopped, so the redo can include it up front (in a `context` argument, say) and not get stuck on the same question. |
| `call.round`, `call.earlierRounds` | What was already settled in this call: earlier questions, with the action and the **names** of fields answered. Never the values. |
| `createdAt`, `expiresAt`, `call.startedAt` | Whether the request is stale enough that redoing it no longer makes sense. |

**Content rules:**
- **Arguments** go through `LlmElicitationResponder.RedactArguments`: credential-named keys are
  redacted, and secret-shaped text inside other values is scrubbed.
- **`userExcerpt`** goes through `McpSecretScrubber` and is capped at 500 characters.
- **Server-written text** (question, field names, options, tool description) is flattened, as
  everywhere else.
- **Never stored:** answer values, and anything from the server's continuation (the bridge never
  has `requestState`). The ledger records *what was asked and why*, not what anyone said in reply.

### Where, and in what shape

- **Location:** a single JSON file on the agent's volume, by default
  `/data/agent/mcp/pending-questions.json` (`McpBridge:PendingLedgerPath`). It's a persisted
  store, so it has a top-level `version` and follows `design/schema-migrations.md`: a change the
  tolerant deserializer can't absorb bumps the version and ships an `ISchemaMigration`.
- **Writes:** the bridge is the only writer. Writes are serialized, and each one goes to a
  temporary file that is then renamed over the old one, so a crash mid-write leaves the
  previous ledger intact.
- **Size is bounded:** at most `MaxPendingQuestions` entries can be pending at once, and resolved
  entries are purged 24 hours after they resolve.

### Lifecycle

```
pending ──answered / declined / expired──► resolved          (normal path; resolvedAt set)
pending ──process restarts──► interrupted ──notice delivered──► notified ──24 h──► purged
```

### Startup reconciliation

Before the bridge accepts tool invocations:

1. **Mark.** Every `pending` entry becomes `interrupted`. No parked call can have survived, so
   this is certain, not a guess. Entries past `expiresAt` become `expired` instead and aren't
   announced, because their question had already lapsed.
2. **Notify.** For each `interrupted` entry whose session is a user conversation
   (`session/{id}`), use the path A2A results use today (`A2ATaskResultHandler`):
   - put the full entry in the session's working memory at `mcp-interrupted/{questionId}`
     (24 h TTL);
   - inject a synthetic turn, so the next agent turn in that session sees it whether or not the
     user is mid-conversation:

   > [rockbot] A tool call was interrupted by a restart. You had called `research` on the
   > `research` server (arguments: question "Tell me about Mercury", context "for a telescope
   > night") because the user asked: "I'm planning a telescope night next week and want to see
   > Mercury…". It was waiting for an answer to "Which Mercury do you mean?" (meaning: planet |
   > element | band). That call is gone and can't be resumed. If it's still needed, call the tool
   > again, and include the answer in the arguments if the tool takes it (for example in
   > `context`), or ask the user first. Full details: `get_from_working_memory("mcp-interrupted/q_7f3c2a…")`.

   Interrupted calls from subagents or scheduled tasks are logged and marked, not announced.
   The run that made them doesn't outlive a restart.
3. **Mark notified.** Delivery is at least once. A crash between notifying and marking may
   repeat a notice, and the working-memory key and `questionId` let a repeat be recognized as
   one.

A late `mcp_answer` for an interrupted question gets the same context back, so whichever way the
agent finds out, it gets the same deterministic explanation.

## Interaction with responders

`handback` replaces the server's responder for that server. The `conversation` responder stays
the right choice when an immediate, in-band answer matters more than letting the agent or user
decide. Research's disambiguation question, for example, is usually settled by the conversation.
The two can't be combined per question in this design; that's worth revisiting once there's
data on how often each path is used.

## Observability

- **Metrics:** questions handed back, answered, declined by the agent, expired, rejected (with
  a reason label), interrupted by a restart, interruption notices delivered, and parked calls
  (gauge).
- **Logs:** server, tool, session, `question_id` and outcome. Never answer values.

## Security summary

- **Server text** (question, fields, options) is untrusted, flattened, and marked as server
  content in the tool result.
- **Answers** pass the same schema validation as every other elicitation answer. Credentials
  are declined before the agent ever sees the question.
- **Session binding** and **unguessable ids**: a question can only be answered by the
  conversation that caused it.
- **Decisions require a user turn** after the hand-back. The agent can't confirm on the user's
  behalf.
- **The pending ledger holds context, never answers.** Arguments are redacted and scrubbed, the
  user excerpt is scrubbed and capped, server text is flattened, and answer values and server
  continuations are never written.
- **Opt-in per server**, never through the bridge-wide default, and never following a server
  name the model re-points. `McpElicitationConfig.WithoutGrants()` must turn `handback` back
  into `auto`, just as it drops a named `responder` and `defaults`. It doesn't touch `mode`
  today, so phase 1 adds that.

## Phases

1. **Bridge:**
   - `PendingQuestionStore`, backed by the durable pending ledger (write-ahead, atomic writes,
     status transitions, purge);
   - hand-back mode in `McpElicitationCoordinator`, with a signal to the invoke path;
   - the invoke path's first-of (result | hand-back);
   - TTL and caps.

   Tested with the MRTR harness from #607 (hand-back, answer, second question, decline, expiry,
   wrong session), plus ledger tests (write-ahead, crash-safe write, purge, schema version).
2. **`mcp_answer`:** a management tool in `McpManagementExecutor`, a bridge management handler,
   validation, and the user-turn check through `IConversationMemory` (the bridge runs in the
   agent process).
3. **Restart recovery:** startup reconciliation (pending → interrupted or expired), the
   working-memory entry and synthetic turn for each user session, notified-marking, and the
   "interrupted" answer to a late `mcp_answer`. Tested by writing a ledger with pending entries,
   starting the bridge, and checking the notices.
4. **Agent guidance:** tool description and a directive line: answer from context when you can,
   ask the user when you can't, never guess a decision, and on an interruption notice decide
   whether the original request still needs doing before redoing the call.
5. **UI (follow-up):** render the structured question as a form in the Blazor UI, so the user
   answers the server's form directly. That's the strongest guarantee for decision fields.

## Open questions

1. **Resume after a restart.** With the pending ledger, a restart is detected and announced, but
   the interrupted call must be redone. Ask the SDK maintainers for a way to receive
   `InputRequiredResult` instead of auto-resolution. The ledger could then store `requestState`
   (encrypted at rest; it's the server's opaque continuation), and reconciliation could re-park
   the call instead of announcing an interruption.
2. **Asynchronous tools.** When the Tasks-aware bridge lands, a task's input request should use
   the same hand-back, not a parked `CallToolAsync`. The pending store and `mcp_answer` are
   meant to serve both.
3. **Per-question routing.** Could a server's choice-only questions go to `conversation` in-band
   and everything else hand back? That would be simple to add to the coordinator once both
   paths have usage data.
