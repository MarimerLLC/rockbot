# Wisps

Wisps are a **lightweight execution tier** for procedural, multi-step tasks that don't
require full agent context. Where subagents receive the complete `AgentContextBuilder`
pipeline (soul, directives, memory recall, skill index, knowledge graph) on every LLM
round-trip, wisps are harness-supervised pipelines that execute steps with minimal (or
zero) LLM involvement.

**Token savings:** A 5-step subagent task consumes ~60-80K input tokens. The same
workflow as a wisp costs ~2-12K tokens or less, because direct steps bypass the LLM
entirely and LLM steps receive only a tightly-scoped prompt.

---

## When to use wisps vs. subagents

| Wisps | Subagents |
|-------|-----------|
| Steps are known in advance | Task requires discovery/improvisation |
| Mostly tool calls with known parameters | Heavy judgment or multi-turn reasoning |
| Data pipelines: fetch → transform → output | Open-ended research or analysis |
| 2-12K tokens total | 60-80K tokens acceptable |

**Rule of thumb:** If you can write out the exact steps and parameters in advance,
use a wisp. If the task requires exploration or adaptation, use a subagent.

---

## Architecture

```
Calling agent (primary or subagent)
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  LLM calls spawn_wisps(definitions: [{ ... }])          │
│     │                                                   │
│     ▼                                                   │
│  SpawnWispsExecutor.ExecuteAsync()                      │
│     │  Parses WispDefinition[] from JSON                │
│     │  Generates batch ID + per-wisp IDs                │
│     │  Runs wisps concurrently (semaphore-gated)        │
│     │                                                   │
│     ▼                                                   │
│  WispExecutor.ExecuteAsync()                            │
│     │  For each step:                                   │
│     │    ├── Direct: GatewayRouter → IToolExecutor      │
│     │    │   (zero LLM tokens)                          │
│     │    └── Llm: AgentLoopRunner.RunAsync()            │
│     │        (minimal context, Low tier)                │
│     │                                                   │
│     ▼                                                   │
│  Returns WispExecutionResult (synchronous)              │
│     │  Per-step success/failure + content                │
│     │  Failure classification (structural/external/…)    │
│     │  Working memory namespace for detailed output      │
│                                                         │
│  [calling agent receives structured result immediately]  │
└─────────────────────────────────────────────────────────┘
```

Unlike subagents (which are asynchronous — they return a task ID and deliver results
later via messaging), wisps are **synchronous**. The calling agent blocks until all
steps complete and receives the full result in the tool response.

---

## Step modes

### Direct mode

The harness invokes a tool with the exact parameters provided in the step definition.
No LLM is involved — zero token cost. Use for all deterministic operations where you
know the tool name, server, and parameters in advance.

### LLM mode

A lightweight LLM receives a tightly-scoped prompt (wisp directives + step instruction
+ data from prior steps) and makes tool calls. The wisp LLM has **no agent context** —
no soul, no memory, no conversation history, no skill index, no directives. It receives
only:

- ~200-token wisp directives (execute steps, call only listed tools, stop on error)
- The step prompt (provided by the calling agent)
- Data from `input_from` (injected into the prompt or chunked into working memory)
- Current date/time and timezone (injected by `AgentLoopRunner`)
- A scoped tool set (see Tool scoping below)

The LLM step always runs at `ModelTier.Low` with no follow-up pass and no completion
evaluator.

---

## Gateways

Direct mode steps route through one of four gateway types, each mapping to a specific
registered tool:

| Gateway | Registered tool | Required fields |
|---------|----------------|-----------------|
| `Mcp` | `mcp_invoke_tool` | `server`, `tool`, `params` |
| `A2A` | `invoke_agent` | `agent`, `skill`, `message` |
| `Script` | `execute_{language}_script` | `params.script` |
| `Web` | `web_search` or `web_browse` | `tool`, `params` |

The `GatewayRouter` builds the correct `ToolInvokeRequest` arguments for each gateway
type. For example, an MCP step with `server: "ms365"` and `tool: "search_emails"` is
translated to `mcp_invoke_tool({ server_name: "ms365", tool_name: "search_emails",
arguments: { ... } })`.

---

## Tool scoping

LLM steps can only call tools that are explicitly in scope. The tool set is built from:

1. All tools implied by **direct steps'** gateway declarations (e.g., if any direct
   step uses the MCP gateway, `mcp_invoke_tool` is in scope)
2. Tools listed in the top-level **`tools`** array (for tools only LLM steps need)
3. **Working memory tools** (`GetFromWorkingMemory`, `SearchWorkingMemory`, etc.) —
   always available

This prevents the LLM from calling tools the wisp definition didn't anticipate.

---

## Data flow

### `output_to`

When present on any step (direct or LLM), the harness writes the step's result to a
file on the shared volume after the step completes:

- **Direct steps:** Tool response content is written to the file path
- **LLM steps:** LLM output text is written to the file path

The result is also stored in working memory at `wisp/{wispId}/{stepId}/output` for
fast inter-step access without re-reading from disk.

### `input_from`

When present on a step, the harness resolves input data before the step executes:

- **Direct steps:** Template substitution in params (e.g., file path reference)
- **LLM steps:** The harness reads the file from the shared volume. Small content
  (≤8K chars) is injected directly into the prompt. Large content is chunked via
  `ContentChunker` into the wisp's working memory namespace, and the LLM receives
  an index table with chunk keys to retrieve via `GetFromWorkingMemory`.

### Template substitution

Step parameters and prompts support template references to prior step outputs:

- `{{steps.<id>.result}}` — Replaced with the step's output content
- `{{steps.<id>.output_to}}` — Replaced with the step's `output_to` file path

### Transition patterns

| Transition | Data path |
|-----------|-----------|
| Direct → Direct | Shared volume files (scripts read/write directly) |
| Direct → LLM | Harness reads file, chunks into working memory if large |
| LLM → Direct | Harness writes LLM output to shared volume |
| LLM → LLM | Working memory (no file round-trip) |

---

## Error handling

### On-failure branching

Direct steps support `on_failure` with two actions:

- `{ "action": "abort" }` — Stop the pipeline and return the error (default)
- `{ "action": "skip_to", "skip_to": "step_id" }` — Skip intermediate steps and
  resume at the named step

### Failure classification

Every failure is automatically classified by the harness:

| Category | Examples | Learnable? |
|----------|----------|-----------|
| **Structural** | Wrong tool name, missing params, bad gateway config | Yes — skill bug |
| **External** | Timeout, service unavailable, rate limit | No — transient |
| **Judgment** | LLM picked wrong result, missed key data | Partially — prompt quality |
| **Data** | Unexpected format, empty results, schema mismatch | Yes — assumption bug |

### Working memory cleanup

On success, the wisp's working memory namespace (`wisp/{wispId}`) is cleared. On
failure, it is preserved for debugging — the calling agent can read the namespace to
inspect intermediate results.

---

## Failure tracking (Phase 4)

Every wisp execution (success or failure) produces a persistent `WispExecutionRecord`
written to `IWispExecutionLog` (JSONL-backed via `FileWispExecutionLog`). Records include:

- Wisp ID, description, and a SHA-256 definition hash
- Success/failure status with step-level detail
- Failure classification and error message
- Session ID for correlation
- `RetryOf` link when the wisp is detected as a successful retry of a prior failure

### Correction pair capture

When a wisp succeeds and a prior failure with the same definition hash exists in the
same session, `SpawnWispsExecutor` links them as a correction pair and emits a
`WispCorrection` feedback signal via `IFeedbackStore`. The signal includes the prior
failure's category, error message, and failed step — giving the dream system structured
evidence of what went wrong and how it was fixed.

---

## Dream-time learning (Phase 5)

The `DreamService` includes a `RunWispFailureAnalysisPassAsync()` pass that:

1. Queries the last 14 days of wisp execution records
2. Groups by description to identify recurring patterns
3. Sends a summary to the LLM with a structured analysis directive
4. The LLM returns:
   - **Failure patterns** — recurring failures with category, frequency, and
     recommendations
   - **Skill updates** — annotations to append to existing skills (negative examples,
     corrected patterns)
   - **Promotion candidates** — consistently successful wisp patterns that could become
     stored wisp skills (Phase 6, deferred)

Configuration: `DreamOptions.WispFailureAnalysisEnabled` (default `true`) and
`DreamOptions.WispFailureDirectivePath` (default `wisp-failure-dream.md`).

---

## DI registration

```csharp
agent.AddWisps(opts =>
    opts.SharedVolumePath = "/rockbot/shared");
```

This registers:
- `WispExecutor` — core pipeline execution engine
- `FileWispExecutionLog` as `IWispExecutionLog` — persistent execution records
- `WispToolRegistrar` — registers `spawn_wisps` in the tool registry
- `WispToolSkillProvider` — provides `get_tool_guide("wisp")` documentation

The `spawn_wisps` tool is available to both the primary agent and subagents (source
`"wisp"` passes the subagent tool filter).

---

## Project structure

```
src/RockBot.Wisp/
├── WispDefinition.cs          # Top-level pipeline definition
├── WispStep.cs                # Single step (id, mode, gateway, params, etc.)
├── StepMode.cs                # Direct / Llm enum
├── GatewayType.cs             # Mcp / A2A / Script / Web enum
├── OnFailureAction.cs         # abort / skip_to
├── FailureCategory.cs         # Structural / External / Judgment / Data
├── WispExecutor.cs            # Core pipeline executor
├── WispExecutionResult.cs     # Overall result with per-step detail
├── WispStepResult.cs          # Single step result
├── WispStepError.cs           # Error with classification
├── GatewayRouter.cs           # Maps steps to tool invocations + template resolution
├── WispRegistryToolFunction.cs # AIFunction wrapper for scoped LLM step tools
├── SpawnWispsExecutor.cs      # IToolExecutor for spawn_wisps tool
├── WispBatchResult.cs         # Batch result with per-wisp outcomes
├── WispToolRegistrar.cs       # IHostedService tool registration
├── WispToolSkillProvider.cs   # IToolSkillProvider usage guide
├── WispOptions.cs             # Configuration (SharedVolumePath, MaxConcurrentWisps)
├── WispServiceCollectionExtensions.cs # AddWisps() DI extension
├── FileWispExecutionLog.cs    # JSONL-backed execution log
│
src/RockBot.Host.Abstractions/
├── IWispExecutionLog.cs       # Execution log interface
├── WispExecutionRecord.cs     # Persistent execution record
│
tests/RockBot.Wisp.Tests/      # 68 unit tests
```

---

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `WispOptions.SharedVolumePath` | `/rockbot/shared` | Root directory for shared volume file I/O |
| `DreamOptions.WispFailureAnalysisEnabled` | `true` | Enable wisp failure analysis dream pass |
| `DreamOptions.WispFailureDirectivePath` | `wisp-failure-dream.md` | Custom directive for the dream pass |
