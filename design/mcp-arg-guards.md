# MCP Bridge Argument Guards

Per-server, per-tool argument validation enforced by the MCP bridge before a tool call is
forwarded to the server. Declared in mcp.json (`argGuards`), implemented by named
`IMcpArgGuard` handlers resolved from a DI registry.

Operator-facing reference: `docs/tools.md` → "Argument guards". This document records the
design decisions that must survive future refactors.

## Motivation

During a routine patrol (2026-06-11), a subagent called the third-party OneDrive MCP
server's `download_file` with `save_directory: "/tmp"`. The server wrote the file to *its
own pod's* `/tmp` and reported success — but the agent, script pods, and workers share
files through the `rockbot-shared` PVC at `/rockbot/shared`, so the file was unreachable.
The subagent burned minutes (and tokens) trying to locate output that a tool had truthfully
claimed to save. We cannot patch every third-party server; we own the bridge every MCP call
flows through.

## Decisions and rationale

### Named handlers, never `Type.GetType()`

mcp.json is **LLM-writable**: the model can call `register_mcp_server`, and the bridge
persists config back to disk. If a config entry could name an arbitrary CLR type for the
bridge to load and execute, anything that writes mcp.json would have an arbitrary-code
execution channel into the bridge process — the exact host that the "nothing trusts the
LLM" isolation exists to protect. Instead, config selects from a closed set: handlers are
registered in DI (`McpArgGuardRegistration` + `McpArgGuardRegistry`, mirroring the
`TokenProviderRegistry` pattern) and referenced by name. Adding a custom handler requires
compiling it into the image and one `AddSingleton` line — which is also what `Type.GetType()`
would require in practice, minus the gadget risk.

### Fail closed at connect

`ConnectServerAsync` validates `argGuards` (unknown handler, missing handler name, invalid
options) **before** storing the server config. On failure it logs an error and refuses the
connection; the server never enters `_serverConfigs`, so invokes get server-not-found.
Rationale: the operator declared a security policy; connecting without it silently weakens
it — the same "reports success, actually wrong" failure mode this feature exists to fix.
Partial alternatives (disable only guarded tools) create confusing half-connected states,
and a rule with an empty `tools` list covers every tool anyway. The check runs outside the
connection retry loop because a config error is not transient. All connect paths funnel
through `ConnectServerAsync` (startup load, hot-reload, reconnect sweep, on-demand connect,
register flow), so there is one enforcement point.

Invoke-time evaluation also fails closed: an unresolvable handler or a guard that throws
rejects the call with an explanatory message rather than letting it through.

### Guards run before the attachment gateway

In `HandleToolInvokeAsync`, guard evaluation sits after the self-referential `invoke_tool`
unwrap (so guards see the effective inner arguments) and **before**
`AttachmentGateway.RewriteRequestAsync` (so guards see the model's original arguments,
not the gateway's mutations). Rejections publish `ToolError { Code = invalid_arguments,
IsRetryable = false }` — the same shape as attachment rewrite failures — which
`McpToolProxy` converts into the tool-result text the model reads. Rejection messages are
written to teach: they name the offending argument, the allowed prefixes, and *why* the
value is wrong ("pod-local to the MCP server, invisible to the agent and script pods"), so
the model self-corrects in one turn instead of flailing.

### Single enforcement point covers all callers

Every MCP invocation in the framework — primary agent, subagents, workers, wisps, A2A
handlers, self-repair — flows through `McpToolProxy` → `tool.invoke.mcp` → the bridge's
one invoke handler. No component holds its own `McpClient`. The transparent
reconnect-retry inside the handler reuses the already-validated `arguments` instance, so
it cannot bypass a guard (a rejected call never reaches the retry path).

### Re-registration preserves guards

`register_mcp_server` cannot express `argGuards`, and it is model-callable. Without
protection, re-registering an existing server name would overwrite its config and silently
strip operator-declared policy. The register flow copies `ArgGuards` from the existing
config when the name matches. A renamed duplicate of the same URL is rejected by the
canonical-identity dedup check. **Known follow-up**: `Attachments` and `Auth` have the same
re-registration exposure and are not yet preserved; fixing that is out of scope here.

### Excluded from `CanonicalIdentity()`

Like `Attachments`, `ArgGuards` is policy about *how* the server is invoked, not *which*
server it is. A guard-bearing entry and a guard-free clone of the same server must count as
duplicates, otherwise the dedup pass could keep the unguarded copy. Pinned by
`CanonicalIdentity_DiffersOnlyByArgGuards_AreEqual`.

### Mutable arguments by design

`McpArgGuardContext.Arguments` is the live dictionary the bridge forwards (the
AttachmentGateway precedent). Built-in handlers only inspect it, but the contract permits
future transforming handlers (e.g. normalizing relative paths against the shared volume)
without interface changes. Pinned by `Evaluate_GuardMutatesArguments_MutationVisibleToCaller`.

## Built-in handler: `path-prefix`

Rejects when a configured string argument falls outside the allowed absolute prefixes.

- Lexical normalization (backslashes → `/`, resolve `.`/`..`, collapse separators) —
  deliberately **not** `Path.GetFullPath`, because the target filesystem is the Linux MCP
  server pod, not the machine running the bridge or its tests.
- `Ordinal` comparison (Linux paths are case-sensitive; intentionally differs from
  `FileWriteToolExecutor.SafeResolvePath`, which targets the local filesystem) and
  boundary-aware matching (`/rockbot/shared` ≠ `/rockbot/shared-evil`).
- Relative paths reject (they resolve against the server pod's cwd — pod-local by
  definition). Traversal escaping the prefix rejects; traversal staying inside passes.
- Missing arguments pass unless `requireArgs: true` — which closes the "model omits
  `save_directory` and the server defaults to a pod-local path" hole. The guard cannot
  know server-side defaults, so requiring the argument is opt-in per rule.
- Empty `allowedPrefixes` is a config error, not allow-all.

## v1 non-goals

- **No response-side validation** (e.g. verifying a reported saved path actually exists on
  the shared volume). If needed later, it is a separate context/method — leave
  `IMcpArgGuard` focused on request arguments.
- **No rewriting in built-in handlers** — a silently redirected path would make the tool
  result inconsistent with what the model asked for, the same confusion class this feature
  removes.
- **No implicit default prefixes** from `FileSystem:BasePath` — guards are explicit
  per-server config; an implicit default would make policy invisible.
- **No `argGuards` parameter on `register_mcp_server`** — guards are operator policy,
  declared only via mcp.json on the PVC.
