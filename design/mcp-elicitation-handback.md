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

Consequence: a pending question lives in the bridge's memory. A restart of the agent process
(the bridge runs inside it) loses it. The agent is told "that question expired, call the tool
again", which is no worse than today. If a later SDK exposes the `InputRequiredResult` (worth
asking upstream), pending questions could be persisted and survive restarts, with nothing else
in this design changing.

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

1. **The question exists and hasn't expired.** Otherwise: "expired — call the tool again".
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

## Interaction with responders

`handback` replaces the server's responder for that server. The `conversation` responder stays
the right choice when an immediate, in-band answer matters more than letting the agent or user
decide. Research's disambiguation question, for example, is usually settled by the conversation.
The two can't be combined per question in this design; that's worth revisiting once there's
data on how often each path is used.

## Observability

- **Metrics:** questions handed back, answered, declined by the agent, expired, rejected (with
  a reason label), and parked calls (gauge).
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
- **Opt-in per server**, never through the bridge-wide default, and never following a server
  name the model re-points. `McpElicitationConfig.WithoutGrants()` must turn `handback` back
  into `auto`, just as it drops a named `responder` and `defaults`. It doesn't touch `mode`
  today, so phase 1 adds that.

## Phases

1. **Bridge:**
   - `PendingQuestionStore`;
   - hand-back mode in `McpElicitationCoordinator`, with a signal to the invoke path;
   - the invoke path's first-of (result | hand-back);
   - TTL and caps.

   Tested with the MRTR harness from #607 (hand-back, answer, second question, decline, expiry,
   wrong session).
2. **`mcp_answer`:** a management tool in `McpManagementExecutor`, a bridge management handler,
   validation, and the user-turn check through `IConversationMemory` (the bridge runs in the
   agent process).
3. **Agent guidance:** tool description and a directive line: answer from context when you can,
   ask the user when you can't, and never guess a decision.
4. **UI (follow-up):** render the structured question as a form in the Blazor UI, so the user
   answers the server's form directly. That's the strongest guarantee for decision fields.

## Open questions

1. **Persistence.** Pending questions die with the process. Ask the SDK maintainers for a way to
   receive `InputRequiredResult` instead of auto-resolution; with it, `requestState` could be
   persisted and re-issued after a restart.
2. **Asynchronous tools.** When the Tasks-aware bridge lands, a task's input request should use
   the same hand-back, not a parked `CallToolAsync`. The pending store and `mcp_answer` are
   meant to serve both.
3. **Per-question routing.** Could a server's choice-only questions go to `conversation` in-band
   and everything else hand back? That would be simple to add to the coordinator once both
   paths have usage data.
