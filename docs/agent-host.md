---
title: Agent host
nav_order: 4
---

# Agent host

The agent host is the runtime that wires together messaging, LLM calls, memory, skills, tools,
and the dream cycle into a working agent process. It lives in `RockBot.Host` and
`RockBot.Host.Abstractions`, with the concrete `RockBot.Agent` project providing the runnable
executable.

---

## Overview

```
Incoming MessageEnvelope (from RabbitMQ)
    │
    ▼
IMessagePipeline.DispatchAsync()
    │
    ├── Middleware chain (logging, tracing, error handling, ...)
    │
    ▼
IMessageHandler<TMessage>.HandleAsync()
    │   UserMessageHandler — main LLM conversation loop
    │   ScheduledTaskHandler — scheduled task delivery
    │   ConversationHistoryRequestHandler — history replay
    │
    ├── IConversationMemory — sliding window of turns
    ├── ILongTermMemory — BM25 recall of relevant memories
    ├── ISkillStore — BM25 recall of relevant skills
    ├── IWorkingMemory — global path-namespaced scratch space (TTL-based)
    ├── ILlmClient — serialized LLM gateway (one in-flight at a time)
    └── IFeedbackStore — quality signal writes (fire-and-forget)
```

---

## Agent identity and profile

### `AgentIdentity`

```csharp
public sealed record AgentIdentity(
    string Name,          // Logical agent name, e.g. "rockbot"
    string InstanceId     // Unique instance; auto-generated GUID if not supplied
);
```

Used in system prompt construction, topic subscriptions, and as the `Source` field on outgoing
envelopes.

### `AgentProfile`

The agent's personality and instructions are loaded from markdown files on the data volume:

| File | Purpose |
|---|---|
| `soul.md` | Core identity, values, and personality — stable; authored by prompt engineers |
| `directives.md` | Deployment-specific operational instructions |
| `style.md` | *(optional)* Voice and tone polish |
| `memory-rules.md` | *(optional)* Rules governing when and how memories are formed |

The profile is parsed into an `AgentProfile` composed of `AgentProfileDocument` instances. Each
document is split on `##` headings into named `AgentProfileSection` items. Sections can be
looked up by name across all documents via `profile.FindSection("name")`.

### `DefaultSystemPromptBuilder`

Assembles the system prompt from the agent profile and identity:

```
You are {AgentName}.

{soul.md content}

{directives.md content}

{memory-rules.md content}   ← if present

{style.md content}          ← if present
```

The result is cached after the first call — the profile is immutable at runtime. The built
system prompt is the starting system message on every LLM request.

---

## Message pipeline

### Registration

```csharp
agent
    .HandleMessage<UserMessage, UserMessageHandler>()
    .HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>()
    .HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>()
    .UseMiddleware<LoggingMiddleware>()
    .UseMiddleware<TracingMiddleware>()
    .UseMiddleware<ErrorHandlingMiddleware>()
    .SubscribeTo(UserProxyTopics.UserMessage)
    .SubscribeTo(UserProxyTopics.ConversationHistoryRequest);
```

### Dispatch flow

`IMessagePipeline` receives a raw `MessageEnvelope` from the subscriber callback:

1. Deserializes the `MessageType` field to find the registered `IMessageHandler<T>`
2. Passes the envelope through the middleware chain
3. Middleware calls `next()` to continue; or short-circuits by returning a `MessageResult`
4. The innermost middleware invokes the handler

`MessageTypeResolver` maps `MessageType` strings to .NET types. Registration is done via
`agent.HandleMessage<TMessage, THandler>()` which records both the type mapping and the DI
registration for `THandler`.

---

## Conversation memory

### `FileConversationMemory` (implements `IConversationMemory`)

Wraps `InMemoryConversationMemory` with file-backed persistence:

- Each session serializes to `{BasePath}/{sessionId}.json`
- On startup, sessions whose last turn falls within `SessionIdleTimeout` are reloaded — so
  recent conversations survive agent restarts
- Per-session `SemaphoreSlim` prevents concurrent write races on the same file
- If `IConversationLog` is registered, every turn is also appended to the conversation log for
  the dream preference-inference pass

**Session lifecycle:**
1. First message in a session creates the file
2. Subsequent messages append turns and re-serialize
3. `ClearAsync` removes both the in-memory state and the file
4. Stale sessions (beyond `SessionIdleTimeout`) are not loaded on restart

---

## Feedback and session evaluation

### `FileFeedbackStore` (implements `IFeedbackStore`)

Appends `FeedbackEntry` records to per-session JSONL files:

```
{BasePath}/{sessionId}.jsonl
```

One JSON object per line. Per-session semaphores prevent concurrent write races.

`QueryRecentAsync` scans all JSONL files to find entries since a given timestamp — used by the
dream cycle to gather quality signals for memory consolidation and skill optimization.

These per-session files (along with the skill-usage and tool-call logs) are append-only and are
capped by the dream cycle's [log-retention pass](dream-service.md#pass-0--log-retention) — aged
files are deleted, the directory is held under a file-count cap, and each surviving file is
line-trimmed (so a persistent UI/CLI session that never ages out is still bounded).

### `SessionSummaryService`

Background hosted service that evaluates completed sessions:

1. Polls on `FeedbackOptions.PollInterval` (default 5 minutes)
2. Finds sessions whose last turn is older than `SessionIdleThreshold` (default 10 minutes)
   that haven't already been evaluated this run
3. Backs off if the LLM is busy (polls every 5s until idle)
4. Sends the full session transcript to the LLM with an evaluator directive
5. Writes a `FeedbackEntry` with `SignalType = SessionSummary` containing:
   - `summary`: one-sentence description
   - `toolsWorkedWell`, `toolsFailedOrMissed`, `correctionsMade`
   - `overallQuality`: `excellent` / `good` / `fair` / `poor`

The evaluator directive is loaded from `session-evaluator.md` on the data volume, with a
built-in fallback.

The dream cycle's skill optimization pass uses `poor` / `fair` quality scores, along with
explicit `Correction` signals, to identify skills that need improvement.

---

## Conversation log

### `FileConversationLog` (implements `IConversationLog`)

Single-file JSONL log of all conversation turns across all sessions:

```
{BasePath}/turns.jsonl
```

A single semaphore serializes all writes. Used exclusively by the dream cycle:
- The preference-inference pass reads the full log to infer durable user preferences
- The skill gap detection pass reads it to find recurring patterns
- Both passes clear the log after processing to prevent unbounded growth

`IConversationLog` is **opt-in** — call `WithConversationLog()` explicitly in the host
builder. `WithMemory()` does not register it.

---

## Three-tier LLM routing

### `ModelTier`

```csharp
public enum ModelTier { Low, Balanced, High }
```

Every LLM call is tagged with a tier. The `TieredChatClientRegistry` singleton holds one
`IChatClient` per tier and `LlmClient` selects the right one at call time.

| Tier | Intended use | Falls back to |
|---|---|---|
| `Low` | Short factual questions, trivial single-step tasks | Balanced |
| `Balanced` | Moderate-complexity requests, patrol tasks | — (required) |
| `High` | Deep analysis, dream consolidation, research | Balanced |

Low and High are optional in configuration; when absent they fall back to Balanced.

### `KeywordTierSelector` (implements `ILlmTierSelector`)

Scores prompts using a keyword + length heuristic — no embeddings, no external calls:

- **Length score** (0 – 0.40) — longer prompts tend to be more complex
- **Keyword score** (0 – 0.35) — high-signal words (`analyze`, `research`, `distributed`, …)
  increase the score; simplex words (`define`, `capital of`, `list the`, …) decrease it
- **Structural score** (0 – 0.25) — code blocks, math notation, multi-step markers

Scores at or below `lowCeiling` → Low; at or below `balancedCeiling` → Balanced; above → High.

For **user messages**, thread state then raises the tier (#663). Each decision records the
rule that decided it (`TierClassification.Rule`, logged as `rule=` and stored as
`routingRule` in the routing log):

| Rule | Effect |
|---|---|
| `score-band` / `high-gate` / `trivial-guard` | The score band, capped at Balanced without a complexity signal, or forced Low when trivial |
| `active-thread-floor` | On an established thread, any non-trivial message routes at least Balanced (no length limit) |
| `active-subagent-floor` | While the session has a subagent running, at least Balanced |
| `balanced-floor-keyword` | A dream-learned floor keyword lifts Low to Balanced (#486) |
| `research-question-floor` | A question naming a technical subject (acronym, version, spec/protocol/API) lifts Low to Balanced |
| `inherited-tier` | At least the highest tier the session's last three turns earned on their own (within 30 min) — kept in the in-memory `SessionTierHistory` |
| `trivial-ack` | A pure acknowledgement/greeting ("thanks", "ok", "👍") skipped the floors and stayed Low |

A turn routed Low that makes a side-effecting tool call (by verb in the tool name — see
`ToolSideEffects`) or hits two tool errors finishes its loop on Balanced
(`TierEscalationContext`; logged as `Tier escalated Low→Balanced mid-turn: …`). Pinned tiers
(`LLM:FixedTier`) are exempt from all of this.

The parameterless constructor always uses compiled-in defaults (used in tests). The DI
constructor hot-reloads `{BasePath}/tier-selector.json` every 60 seconds so thresholds and
keyword lists can be tuned without a pod restart. Keyword lists in the file are merged with
the compiled defaults; retired low signals (`what is`, `what's`, `tell me about`, `look up`,
`show me`, `i think`, …) are dropped from the merged list even when the file names them.

### `tier-selector.json` (hot-reloadable)

```json
{
  "version": 1,
  "notes": "2026-02-24: tightened balancedCeiling after dream review",
  "lowCeiling": 0.15,
  "balancedCeiling": 0.46,
  "highSignalKeywords": ["analyze", "research", "distributed", "..."],
  "lowSignalKeywords":  ["define", "list the", "..."]
}
```

All fields are optional — omitted fields fall back to compiled defaults.

### Dream self-correction pass

Each routing decision is appended to `tier-routing-log.jsonl` on the PVC (capped at 200
entries). The dream cycle's tier-routing review pass reads the log and — when it detects
systematic mis-routing — rewrites `tier-selector.json` with corrected thresholds and keyword
lists. The pass skips when fewer than 10 entries exist.

---

## LLM client

### `ILlmClient`

```csharp
public interface ILlmClient
{
    bool IsIdle { get; }
    Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);
    Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options = null,
        CancellationToken ct = default);
}
```

A serialized gateway around the underlying `IChatClient` from `Microsoft.Extensions.AI`.
Enforces that only one LLM call is in flight at a time within the agent process:

- If a second call arrives while the first is running, it queues and waits
- `IsIdle` lets background services (dream cycle, session evaluator) back off while the user
  is waiting for a response

The tier-less overload defaults to `ModelTier.Balanced`. Calls log `tier=Balanced model=...`
so routing decisions are visible in the pod logs.

---

## AgentLoopRunner

`AgentLoopRunner` is the **single entry point for all LLM tool-calling interactions** in the
agent process. Every message handler (`UserMessageHandler`, `ScheduledTaskHandler`,
`SubagentRunner`, A2A handlers, etc.) calls `AgentLoopRunner.RunAsync` rather than
`ILlmClient.GetResponseAsync` directly.

> **Invariant:** Never call `ILlmClient.GetResponseAsync` from a message handler to drive
> a tool-calling loop. Always go through `AgentLoopRunner.RunAsync`. Direct calls bypass
> reasoning scaffolding, completion evaluation, hallucination nudging, context overflow
> trimming, and metrics recording.

### What RunAsync does

1. **DateTime context injection** — ensures the model knows the user's current date/time
2. **Reasoning scaffolding** — injects a system message with the iteration budget and
   step-by-step planning encouragement
3. **Inner tool loop** — dispatches to either the native path (`FunctionInvokingChatClient`)
   or the text-based parsing loop depending on `ModelBehavior.UseTextBasedToolCalling`
4. **Completion evaluation** — after the inner loop returns, a cheap `ModelTier.Low` LLM
   call evaluates whether the response actually completes the original user request. If
   incomplete, a continuation nudge is appended and the tool loop re-enters (up to
   `MaxCompletionReprompts` times, default 2). Evaluation is skipped on force-termination
   (consecutive timeouts) and fails open on any evaluator error. See
   [When the completion evaluator runs](#when-the-completion-evaluator-runs).
5. **Proactive follow-up** — after the completion evaluator says COMPLETE, a second
   `ModelTier.Low` call assesses whether there are high-value proactive actions the agent
   could take within the current context (e.g. looking up a contact mentioned in conversation,
   cross-referencing calendar events, connecting related information). If found, context is
   enriched with relevant skills/services and the tool loop runs one more pass. The follow-up
   response is appended to the original. Skipped for simple exchanges. Fails open on error.

### When the completion evaluator runs

The evaluator is one `ModelTier.Low` call, so `RunAsync` gates it (`CompletionEvalTriggers.Decide`,
#666). It runs when any trigger below fires, and logs
`Completion evaluator: RUN (trigger=<name>)`; otherwise it logs `SKIPPED (…, no trigger)`.
Triggers are checked in this order, and the first match is logged:

| Trigger | Fires when |
|---|---|
| `subagent-synthesis` | The loop relays a subagent's result (`SubagentResultHandler`). It's judged against the user request that led to the spawn. |
| `promise-no-action` | The loop made no tool calls and the reply promises or describes work: "I should have…", "I'm cutting it… now", "I've got the right path", "I'll search…", or a closing "Let me…". |
| `bare-claim` | The reply opens with a bare completion claim such as "Done." or "Updated.". |
| `imperative-no-tools` | The loop made no tool calls and the user gave an instruction ("do it", "go ahead", "figure out…", "can you trim it?") or pushed back ("why didn't you…", "30 slides is a lot", "try again"). |
| `side-effect` | The loop made a side-effecting tool call. `ToolSideEffects` decides this from the verb in the tool name; for `mcp_invoke_tool` it reads the `tool_name` argument. |
| `pattern` | The pre-#666 gate: the hallucinated-action or capability-denial regex matched, the reply is under 20 characters (unless the user only said "thanks", "ok" or "hi"), or the loop hit its iteration cap. |

The agent's own bookkeeping (task list, working memory, long-term memory, progress reports)
doesn't count as a tool call for the "no tool calls" triggers, and it isn't a side effect. A loop
that calls `spawn_subagent` or `invoke_agent` is still skipped, because its results arrive later.
The loop records its tool calls in `LoopToolCallLedger` instead of reading the chat history,
because context trimming can shorten the history.

The evaluator reads the user's last three messages, the agent's previous message, every tool
call the loop made (name, ok or failed, and whether it changed state), what the user asked for,
and the reason the check is running. Its rubric asks five questions:

1. What did the user ask for (counts, format, location, constraints)?
2. Was it delivered?
3. Does any number in the reply contradict the user's constraints?
4. Do the tool calls support every action the reply claims?
5. Did the reply promise work it could have done now?

**What the user asked for (#683).** `CompletionEvalTriggers.ClassifyUserRequest` labels the
request being judged `UserAskedFor: instruction` or `UserAskedFor: information-only`. It reuses
the #666 `ImperativeInstructionRegex`; a bare "yes"/"sure"/"please" right after an agent message
also counts as an instruction, and an unknown request keeps the full rubric. For a synthesis turn
the request is the one behind the spawn (`OriginatingUserRequest`), otherwise the latest user
message. When the request is information-only (it shares context, gives information or asks a
question), questions 1–2 are skipped, except that a question asked must be answered. The reply is
judged only on whether its facts and claims are supported and stated accurately (questions 3–5).
"The user's goal implies more work" is never a reason for INCOMPLETE. Before this, a context-only
message ("the talk doesn't exist yet") was judged INCOMPLETE for not delivering demos, and the
re-prompt spawned a subagent nobody asked for.

**Subagent evidence (#683).** A synthesis turn's own tool calls are usually only
`get_from_working_memory`. Every honest relay of a subagent's writes and uploads therefore looked
like an unsupported claim, and the re-prompt made the agent disown real work. Now:

- `AgentLoopRunner.RunAsync` exposes its ledger on `LoopDiagnostics.ToolCallLedger`.
- `SubagentRunner` carries a compact copy on `SubagentResultMessage.ToolCalls`
  (`SubagentToolCallSummary`: name, ok/failed, changes-state, args up to 100 chars) and
  `ToolCallCount`. The list holds at most 40 calls. Over the cap, failed and state-changing calls
  are kept first, then other calls, then bookkeeping, newest first within each group.
  Both properties are additive; results from an older build deserialize with `null`.
- `SubagentResultHandler.BuildRelayedWork` adds the session-work-registry artifacts written by
  that subagent's session or by the wisps and workers linked under it
  (`ISessionWorkRegistry.IsSessionWithin`).
- It passes the result to `RunAsync(relayedWork: …)`.

The evaluator gets a section for each subagent: "Tool calls made by subagent `<id>` (the relayed
work)". All of these sections share about 3,000 characters, and each subagent gets at least 500.
When a section is over its share, state-changing and failed calls are kept first, and the section
says how many calls were left out. The rubric says a relayed claim is supported when that
subagent's calls show it. The agent's own calls are judged separately, only for what the agent
says it did itself this turn. A claim that neither list supports is still INCOMPLETE. If a
subagent made no tool calls, its section says so. If its result didn't report its calls (an
older build), the section says "not reported", and the evaluator is told not to treat their
absence as proof the work didn't happen.

**Re-prompt (#683).** After an INCOMPLETE verdict, the re-prompt adds guidance specific to the
trigger and to what the user asked for. For an information-only request it says to fix what was
flagged and not to start unrequested work. For a synthesis turn it adds: "work the subagent's
own tool calls show it did is real — do not disown it". The message starts with
`[Internal completion check — not a message from the user]` and quotes the user's original
message. It tells the model to reply to that message directly; that the user has not seen the
draft; not to address or thank the user for feedback; not to say "you're right" or "you were
right"; and not to mention the review. It is still sent in the user role, because the history
ends on the assistant draft and some provider paths hoist or reject a trailing system message.
The re-prompt budget is unchanged.

`spawn_subagent` reads the user request of the loop that ran it from
`OriginatingUserRequestContext`. The request is stored on `SubagentEntry` and carried on
`SubagentResultMessage.OriginatingUserRequest`, so the synthesis turn's check sees what the user
asked, not only how the primary agent described the task to the subagent.

Every RUN, COMPLETE and INCOMPLETE log line includes `trigger=` and `userAskedFor=`.

### Completion evaluator configuration

| Setting | Location | Default | Purpose |
|---|---|---|---|
| `AgentHost:MaxCompletionReprompts` | `AgentHostOptions` | 2 | Max re-prompts (0 = disabled) |
| `MaxCompletionRepromptsOverride` | `ModelBehavior` | null (use host default) | Per-model override |
| `AgentHost:MaxFollowUpPasses` | `AgentHostOptions` | 1 | Max proactive follow-up passes (0 = disabled) |
| `MaxFollowUpPassesOverride` | `ModelBehavior` | null (use host default) | Per-model override |

### Diagnostics counters

| Counter | Fires when |
|---|---|
| `rockbot.agent.completion_check.complete` | Evaluator says task is done (tagged `rockbot.completion_check.trigger` and `rockbot.completion_check.user_asked_for`) |
| `rockbot.agent.completion_check.incomplete` | Evaluator triggers a re-prompt (tagged `rockbot.completion_check.trigger` and `rockbot.completion_check.user_asked_for`), so a trigger with a high false-positive rate is visible |
| `rockbot.agent.completion_check.skipped` | Evaluation skipped (force termination) |
| `rockbot.agent.follow_up.triggered` | Follow-up evaluator found an opportunity |
| `rockbot.agent.follow_up.none` | Follow-up evaluator found nothing worth doing |
| `rockbot.agent.follow_up.skipped` | Follow-up evaluation skipped (disabled, force term) |
| `rockbot.agent.consequential_action.gated` | The consequential-action gate refused an external change (tagged `rockbot.tool.name` and `rockbot.action_gate.origin`) |

### Consequential-action gate

A user message that only shares context ("here's the abstract; the talk doesn't exist yet") must
not lead to real calendar events, sent mail or uploads (#685). Before #685 the persona's
"proactive" directives did exactly that, through a subagent and its wisps, and the evaluator
(correctly) judged only the reply's facts. The gate separates *noticing* (always fine) from
*changing an external system* (needs a request).

**What is gated.** `ConsequentialActions.IsConsequential` — a call is consequential when
`ToolSideEffects` says it writes **and** it reaches an MCP server (`mcp_invoke_tool`, judged by its
`tool_name`, or a typed `{server}__{tool}` wrapper), or it creates or cancels one of the agent's
scheduled tasks (`schedule_task`, `cancel_scheduled_task` — automation that would later act
unasked), or it is listed in `AgentHost:ConsequentialActionGate:ExternalTools`. Everything else is
agent-local and never gated: `file_*` on the agent's volume (`drafts/`), working and long-term
memory, the task list, skills, rules, progress reports, sandboxed scripts, and delegation itself
(`spawn_subagent`, `spawn_wisps`, `spawn_workers`, `invoke_agent`) — the gate applies to what the
delegate does. Reads (`list_events`, `get_message`, …) always run.

**When it refuses.** Every run has an `ActionGateScope(RunOrigin, UserAskedFor)`, bound to the async
flow by `RunAsync` (`ActionGateContext`):

| Origin | Set by | External changes |
|---|---|---|
| `UserTurn` | `UserMessageHandler`, `UserFeedbackHandler`; a synthesis turn (`SubagentResultHandler`) relaying a user-turn subagent | Only when the originating request is an `instruction` |
| `SubagentOfUserTurn` | `SubagentManager` captures the spawning run's scope at spawn; `SubagentRunner` passes it on | Inherits the spawning turn's classification |
| `Scheduled` | `ScheduledTaskHandler` (scheduled tasks, patrol) | Allowed — the user configured it |
| `A2A` | `RockBotTaskHandler`, `ResearchAgentTaskHandler` (inbound tasks) | Allowed |
| `Unknown` | Any caller that passes nothing and is not nested in another run | Allowed (pre-#685 behaviour) |

A run that passes no scope and runs inside another run's tool call — a wisp LLM step, a worker —
inherits the enclosing scope. For a user turn, the classification is the one the completion
evaluator uses: `ClassifyUserRequest(latest user message, followsAgentMessage)`, so "yes", "sure"
or "go ahead" right after an agent proposal counts as an instruction. A subagent keeps the
*user's* classification; its own imperative task description doesn't launder an information-only
message. `SubagentResultMessage.RunOrigin` / `.UserAskedFor` carry the scope back, so the synthesis
turn runs under the same gate (`ActionGateScope.FromRelayedResults`; siblings that disagree resolve
to instruction; results from an older build keep the old behaviour).

**Where it checks.** `ActionGateContext.Check(tool, args)` runs at every dispatch site:
`RockBotFunctionInvokingChatClient.InvokeFunctionAsync` (native path), both dispatch sites of the
text-based loop, and `WispExecutor`'s direct steps. A refused call is not executed. Its tool result
is:

> Not run: calendar-mcp__create_event would change calendar-mcp but the user did not ask for that.
> Propose it to the user in one sentence (what, when, where) and wait for them to ask. Do not retry
> this call or route it through another tool.

The call is recorded in the run's ledger as failed (so it neither counts as a side effect nor
shows as relayed work that happened), logged at Information as
`Consequential action gated: <tool> (originating request: information-only, origin=<origin>)`,
and counted on `rockbot.agent.consequential_action.gated`. A refused wisp step fails with category
`Judgment`.

**Evaluator backstop.** For a run that needs an instruction and an information-only request, any
*successful* external change in the loop's calls or a relayed subagent's calls is listed under
"External changes the user did not ask for", and the rubric makes that INCOMPLETE ("unrequested
external change: …"). The re-prompt then says to make no further external changes and to tell the
user plainly what was changed and where, so they can keep or undo it. With the gate on, this
should rarely fire.

**Configuration.** `AgentHost:ConsequentialActionGate:Enabled` (default `true`; env
`AgentHost__ConsequentialActionGate__Enabled=false` turns the gate off) and
`AgentHost:ConsequentialActionGate:ExternalTools` (native tool names to gate like MCP writes;
default empty).

**Time conversion.** The same incident relayed 09:45 Amsterdam on 2026-10-28 as 2:45 AM Chicago
(it is 3:45 — Europe left summer time on Oct 25, the US does on Nov 1). The `convert_time`
registry tool (`RockBot.Tools.TimeConversion`, registered by `AddTimeTools()`) converts a wall-clock
time between IANA zones with the DST rules of that date and reports both UTC offsets;
`common-directives.md` tells every rung to use it instead of doing the arithmetic.

---

## Per-model behaviors

Model-specific behavioral overrides are loaded from `model-behaviors/{model-prefix}/` on the
data volume. The model prefix is matched case-insensitively against the deployed model ID.

| File | Applied at |
|---|---|
| `additional-system-prompt.md` | Appended to every system prompt (guardrails, output constraints) |
| `pre-tool-loop-prompt.md` | Injected before each tool-calling iteration |

Additional properties are configurable in `appsettings.json` under `ModelBehaviors:Models:{prefix}`:

| Property | Type | Default | Purpose |
|---|---|---|---|
| `NudgeOnHallucinatedToolCalls` | bool | false | Inject a nudge when the model describes tool actions without emitting calls |
| `NudgeOnLeakedToolSyntax` | bool | false | Retry when the model leaks tool-calling scaffolding (`to=multi_tool_use.parallel`, `to=functions.X`) into its text output. Language-agnostic; targets a documented OpenAI GPT-family failure mode. Safe to enable for any deployment |
| `NudgeOnUnexpectedCjkOutput` | bool | false | Retry when the model emits 3+ consecutive CJK codepoints — a heuristic for English-primary deployments where CJK output correlates with training-data contamination. **Leave off for agents that legitimately respond in Chinese or Japanese** |
| `NudgeOnToolFailureGiveup` | bool? | true | Retry once when the model gives up after a tool returned an error (matches phrasings like "tool failure", "errored on both", "from the current tool state") instead of retrying the tool itself. General-purpose; on by default. Set to `false` to opt out for a specific model |
| `MaxToolIterationsOverride` | int? | null (uses `AgentHost:MaxToolIterations`) | Override the per-request tool-loop iteration cap |
| `ToolResultChunkingThreshold` | int? | null (uses 64 000) | Char count above which tool results are chunked into working memory instead of appended inline |
| `ScheduledTaskResultMode` | enum | `Summarize` | How scheduled task output is presented (`Summarize`, `VerbatimOutput`, `SummarizeWithOutput`) |
| `MaxCompletionRepromptsOverride` | int? | null (uses `AgentHost:MaxCompletionReprompts`) | Override the per-request completion-evaluator re-prompt cap |
| `MaxFollowUpPassesOverride` | int? | null (uses `AgentHost:MaxFollowUpPasses`) | Override the per-request proactive follow-up pass cap |

Example — lowering the chunking threshold for a small-context model:

```json
{
  "ModelBehaviors": {
    "Models": {
      "openrouter/deepseek": {
        "ToolResultChunkingThreshold": 32000
      }
    }
  }
}
```

See [Tool result chunking](tools.md#tool-result-chunking-all-tools) for full details.

---

## Agent host builder

`AgentHostBuilder` is the fluent configuration API. Access it via `AddRockBotHost`:

```csharp
services.AddRockBotHost(agent =>
{
    agent.WithIdentity("rockbot");
    agent.WithProfile();                 // Load soul.md, directives.md, etc. from data volume
    agent.WithRules();                   // Load agent rules from rules/ directory
    agent.WithMemory();                  // Conversation + long-term + working memory
    agent.WithConversationLog();         // Opt-in: enables dream gap detection + pref inference
    agent.WithFeedback();                // IFeedbackStore + SessionSummaryService
    agent.WithSkills();                  // ISkillStore + ISkillUsageStore + StarterSkillService
    agent.WithDreaming(opts =>
    {
        opts.InitialDelay = TimeSpan.FromMinutes(5);
        opts.Interval = TimeSpan.FromHours(4);
    });

    // Message handlers
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>();
    agent.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();

    // Tool subsystems
    agent.AddToolHandler();              // Tool invocation dispatch
    agent.AddMcpToolProxy();             // MCP server bridge
    agent.AddWebTools(opts => { ... });  // Web search + browse
    agent.AddSchedulingTools();          // Scheduled task tools
    agent.AddRemoteScriptRunner();       // Script execution via Scripts Manager

    // Subscriptions
    agent.SubscribeTo(UserProxyTopics.UserMessage);
    agent.SubscribeTo(UserProxyTopics.ConversationHistoryRequest);

    // Optional middleware
    agent.UseMiddleware<LoggingMiddleware>();
    agent.UseMiddleware<TracingMiddleware>();
    agent.UseMiddleware<ErrorHandlingMiddleware>();
});
```

### Extension method reference

| Method | Registers |
|---|---|
| `WithIdentity(name)` | `AgentIdentity` |
| `WithProfile()` | `IAgentProfileProvider`, `AgentProfile`, `ISystemPromptBuilder` |
| `WithRules()` | `IRulesStore`, rules tools |
| `WithConversationMemory()` | `IConversationMemory` (file-backed + in-memory) |
| `WithLongTermMemory()` | `ILongTermMemory` (FileMemoryStore) |
| `WithWorkingMemory()` | `IWorkingMemory` (global, path-namespaced; `HybridCacheWorkingMemory` + `FileWorkingMemory`) |
| `WithMemory()` | All three memory tiers above |
| `WithConversationLog()` | `IConversationLog` (FileConversationLog) |
| `WithFeedback()` | `IFeedbackStore` + `SessionSummaryService` |
| `WithSkills()` | `ISkillStore` + `ISkillUsageStore` + `StarterSkillService` |
| `WithDreaming()` | `DreamService` (IHostedService) |

---

## Agent data volume layout

All persistent agent state lives under a single base path (default `/data/agent` in production,
configurable via `AgentProfileOptions.BasePath`):

```
/data/agent/
├── soul.md                    # Core identity and personality
├── directives.md              # Operational instructions
├── style.md                   # (optional) Voice and tone
├── memory-rules.md            # (optional) Memory formation rules
├── dream.md                   # Dream: memory consolidation prompt
├── skill-dream.md             # Dream: skill consolidation prompt
├── skill-optimize.md          # Dream: skill optimization prompt
├── skill-gap.md               # Dream: skill gap detection prompt
├── pref-dream.md              # Dream: preference inference prompt
├── tier-routing-directive.md  # (optional) Dream: tier routing review prompt override
├── session-evaluator.md       # Session quality evaluation prompt
├── tier-selector.json         # (optional) Hot-reloadable tier routing config
├── tier-routing-log.jsonl     # Routing decision log (auto-managed, capped at 200 entries)
├── mcp.json                   # MCP server connection configuration
├── rules/                     # Agent rules (markdown files)
├── model-behaviors/           # Per-model prompt overrides
│   └── {model-prefix}/
│       ├── additional-system-prompt.md
│       └── pre-tool-loop-prompt.md
├── memory/                    # Long-term memory entries
│   └── {category}/
│       └── {id}.json
├── skills/                    # Learned skills
│   └── {name}.json            # (may be nested: skills/mcp/email.json)
├── skill-usage/               # Skill invocation event log
│   └── {sessionId}.jsonl
├── feedback/                  # Session quality signals
│   └── {sessionId}.jsonl
├── conversations/             # Persisted conversation sessions
│   └── {sessionId}.json
├── working-memory/            # Working memory persistence (path-namespaced, TTL-based)
│   ├── session.json           # Entries for all user sessions (session/{id}/...)
│   ├── patrol.json            # Entries for patrol tasks (patrol/{name}/...)
│   └── subagent.json          # Entries for subagents (subagent/{taskId}/...)
└── conversation-log/          # Aggregated turns for dream passes
    └── turns.jsonl
```

---

## Startup sequence

When the agent process starts:

1. `AgentHostBuilder.Build()` registers all services with the DI container
2. `IHostedService` implementations start in registration order:
   - `StarterSkillService` — seeds starter skills from registered `IToolSkillProvider`s
   - `McpBridgeService` — connects to configured MCP servers
   - `FileConversationMemory` — reloads sessions within `SessionIdleTimeout`
   - `SessionSummaryService` — begins polling for sessions to evaluate
   - `DreamService` — schedules first dream cycle after `InitialDelay`
   - `AgentHostService` — subscribes to configured topics and begins processing messages
3. The agent is now ready to receive messages

---

## Configuration reference

Key configuration sections (from `appsettings.json` or environment variables):

```json
{
  "AgentProfile": {
    "BasePath": "/data/agent"
  },
  "AgentHost": {
    "MaxToolIterations": 50,
    "MaxCompletionReprompts": 2,
    "MaxFollowUpPasses": 1,
    "ConsequentialActionGate": { "Enabled": true, "ExternalTools": [] }
  },
  "RabbitMq": {
    "HostName": "rabbitmq.cluster.local",
    "Port": 5672,
    "UserName": "rockbot",
    "Password": "..."
  },
  "LLM": {
    "Balanced": {
      "Endpoint": "https://openrouter.ai/api/v1",
      "ApiKey": "...",
      "ModelId": "anthropic/claude-haiku-4.5"
    },
    "Low": {
      "Endpoint": "https://openrouter.ai/api/v1",
      "ApiKey": "...",
      "ModelId": "google/gemini-flash-1.5-8b"
    },
    "High": {
      "Endpoint": "https://openrouter.ai/api/v1",
      "ApiKey": "...",
      "ModelId": "anthropic/claude-opus-4-6"
    }
  },
  "Memory": {
    "BasePath": "memory"
  },
  "Skills": {
    "BasePath": "skills",
    "UsageBasePath": "skill-usage"
  },
  "Dream": {
    "Enabled": true,
    "InitialDelay": "00:05:00",
    "Interval": "04:00:00",
    "TierRoutingReviewEnabled": true
  },
  "Feedback": {
    "BasePath": "feedback",
    "SessionIdleThreshold": "00:10:00",
    "PollInterval": "00:05:00"
  }
}
```

`LLM.Balanced` is required. `LLM.Low` and `LLM.High` are optional — when absent they fall back
to Balanced. The flat legacy keys `LLM.Endpoint`, `LLM.ApiKey`, `LLM.ModelId` are still
accepted for backward compatibility and are treated as Balanced.
