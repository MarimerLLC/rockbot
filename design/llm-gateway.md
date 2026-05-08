# LLM Gateway

## Problem

LLM calls in RockBot today are made directly from many call sites — handlers, the dream service, subagent runners, A2A handlers — each going through `ILlmClient` to the underlying SDK. There is no global throttling and no centralized retry policy. Three problems result:

1. **Rate limiting is ad-hoc.** When the provider returns 429, behavior depends on whatever the SDK does by default. Call sites do not coordinate. Bursty work (especially planned parallel dream phases) can overwhelm a tier and produce cascades of failures.
2. **Cross-cutting concerns are scattered.** Retry, backoff, metrics, cost recording — all of these belong in one place but currently are not anywhere.
3. **No clean way for user work to outrun background work.** A user message arriving while the dream cycle is mid-flight needs to get LLM capacity. Today this works only because the work-serializer cancels the dream cycle outright; LLM-level coordination is implicit.

The trigger for this design is the [observation framework](observation-framework.md), whose parallelism makes the rate-limit story untenable without coordination. But the gateway is independently valuable.

## Goals

- Single chokepoint for every LLM call, regardless of caller.
- Per-tier concurrency caps (Low / Balanced / High treated independently).
- Cancellation propagates end-to-end: pending and in-flight calls abort when their `ct` fires.
- User-facing calls effectively preempt background calls without a priority queue.
- Centralized observability: queue depth, slot wait time, in-flight count.
- Bounded queue depth: the system fails fast under sustained rate limiting rather than piling up indefinitely.

## Non-goals

- Priority queues / priority lanes. User-priority semantics are achieved via cancellation (see "User priority"). Lanes were considered and rejected as unneeded complexity.
- Replacing the SDK. The gateway wraps the SDK; it does not reimplement provider clients.
- Per-call routing across providers. Tier selection remains the caller's concern; the gateway only mediates calls within whatever tier the caller chose.
- Solving observation-framework parallelism by itself. The gateway is a prerequisite, not a replacement.
- **Owning rate-limit retry.** The OpenAI SDK's `ClientRetryPolicy` already does this well — `Retry-After` honoring, exponential backoff with jitter, and retries for 429s, 5xx, and transient network errors. Reimplementing it in the gateway was attempted in an earlier draft of this design and rejected: the gateway version was less mature (no jitter, no 5xx, no transient-error coverage) and added code without meaningful benefit beyond the metrics. The SDK retry stays enabled. See "Retry policy" below for the consequences.

## Architecture

Every LLM call passes through the gateway. The gateway holds per-tier `SemaphoreSlim` instances; rate-limit retry happens *inside* the slot, in the SDK pipeline.

```
   Caller (handler / dream phase / subagent)
        │  ILlmClient.GetResponseAsync(request, ct)
        ▼
   ┌─────────────────────────────────────────────────┐
   │  LlmGateway                                     │
   │                                                 │
   │   await _tierSemaphores[tier].WaitAsync(ct);    │
   │   try {                                         │
   │     // metrics: slot wait, in-flight, queue     │
   │     // depth, latency                           │
   │     // SDK pipeline handles 429/5xx retry       │
   │     // internally; ct propagates                │
   │   } finally {                                   │
   │     _tierSemaphores[tier].Release();            │
   │   }                                             │
   └────────────────────┬────────────────────────────┘
                        │
                        ▼
                Provider SDK (with built-in retry policy)
```

The gateway is the only direct consumer of the provider SDK. `ILlmClient` implementations that bypass it must be removed. The slot is held during any SDK-internal retry waits — releasing the slot during a retry would not help, since rate limits are per-tier so any other call in the same tier would hit the same limit.

## Cancellation, not priority lanes

User-priority semantics emerge from cancellation. A priority queue was considered and rejected.

The reasoning: the work-serializer already cancels the dream when a user message arrives (`DreamService.DreamAsync` acquires via `TryAcquireForScheduledAsync`, which yields a CT that fires on preemption). When dream's `ct` fires:

- `SemaphoreSlim.WaitAsync(ct)` causes every dream LLM call currently waiting in the queue to throw `OperationCanceledException` and exit, freeing slots.
- In-flight LLM calls inside dream phases see `ct` cancelled and the SDK request aborts.
- The user-facing call's `WaitAsync` then succeeds promptly.

This gives the same outcome as priority lanes — user calls do not wait behind dream calls — without any of the complexity that priority brings: no ambient priority context, no envelope priority field on cross-bus calls, no subagent priority inheritance question, no priority-aware retry semantics.

The one behavioral difference: priority lanes would let dream continue running its non-contending calls during a brief user turn, whereas cancellation drops the dream's queued work entirely. Since the work-serializer already cancels the entire dream cycle on user preemption, this is not a regression — it matches the existing architecture.

## Retry policy

The OpenAI SDK's `ClientRetryPolicy` (the default in `System.ClientModel`) owns retry. It already provides:

- `Retry-After` header honoring on 429 and 503 responses
- Exponential backoff with jitter when no header is supplied
- Retries on 408, 429, 500, 502, 503, 504, and transient network errors
- Configurable max attempts (default 3)
- Cancellation propagation through the pipeline

This is more thorough than what the gateway could reasonably reimplement. The gateway therefore does *not* wrap retry. The SDK pipeline runs retry inside the gateway slot, which means:

- Retry waits hold the slot. As discussed above, releasing during a retry wait would not help under per-tier rate limiting.
- Cancellation reaches retry waits through the standard `ct` propagation. The gateway does not need its own retry-cancellation logic.

**Implication for cross-provider observability:** the gateway does not see individual retry events — they happen inside the SDK pipeline, below the gateway's instrumentation. If retry counts and retry-after sources become important enough to centralize, they should be added by hooking the SDK pipeline (a `PipelinePolicy`-derived listener that forwards retry telemetry into `HostDiagnostics`), not by reimplementing retry above the SDK. This is recorded as a possible future enhancement.

**Non-OpenAI providers.** `CopilotChatClient` has its own retry inside the client (configured via `LlmTierConfig.MaxRetries`). Other future providers will likewise own their own retry. The gateway is provider-agnostic and remains so.

## Cancellation discipline

This is the load-bearing implementation requirement. Every LLM call site must accept and flow `ct` through to the gateway, and the gateway must flow `ct` through to the SDK. Any path that uses `CancellationToken.None` is a black hole: user preemption stops working for that path, and the cancellation-as-priority story breaks.

Concrete enforcement options:

- **Mandatory `ct` parameter at the gateway boundary.** No overload accepting `default`. Call sites must explicitly pass a token; the analyzer/compiler catches missed propagation.
- **Audit pass on existing call sites.** Every existing `ILlmClient` consumer needs review to confirm `ct` is passed.
- **Test coverage.** A test that cancels a CT during a simulated long LLM call and asserts the call aborts within a bounded time. Run for each tier.

## Bounded queue depth

`SemaphoreSlim`'s implicit waiter queue is unbounded. Under sustained rate limiting, callers pile up and a dream cycle that should run for 5 minutes runs for an hour. Two options for bounding:

- **Fail-fast**: `MaxPendingPerTier` config; once exceeded, new calls fail immediately with a "gateway saturated" error. Caller decides whether to retry, defer, or skip. Simplest.
- **Shed-oldest**: cancel the oldest pending waiter when a new call arrives at the cap. More complex, but means "freshness" wins under load.

Recommendation: start with fail-fast. It is much simpler and the dream cycle can choose to skip a phase rather than block. Shed-oldest is only worth it if a real workload demands it.

## Tier configuration

Each tier has an independent `SemaphoreSlim`. Initial caps (configurable):

- **Low**: higher concurrency cap (e.g. 8). Cheap calls, used heavily by extraction in observation phases and similar batch work.
- **Balanced**: moderate (e.g. 4).
- **High**: lower (e.g. 2). Expensive calls, used for judgment work.

Caps are per-process. Across multiple agent processes against the same provider account, total concurrency is the sum. Per-account rate limits ultimately bound the system; the gateway is a per-process governor, not a global one.

## Metrics

Recorded at the gateway, per call:

- Tier
- Latency (semaphore wait, in-flight, total)
- Token counts (input, output, cached if available)
- Cost (computed from tokens × tier price)
- Retry count (and whether it terminated in success or failure)
- Outcome (success, retry-exhausted, cancelled, network-failure, non-retryable-error)

Aggregated per tier:

- Current queue depth
- In-flight count
- Slots free
- Rolling RPM and TPM

Surface via the existing telemetry pipeline (logs at minimum; metrics endpoint if one exists).

## Implementation phases

1. **Wrap, don't replace.** The gateway is a thin layer in front of `ILlmClient` (or whatever the existing abstraction is). Existing call sites already go through that abstraction; the gateway is plumbed in behind it without touching call sites. ✅ Landed in #355.
2. **Add per-tier semaphores.** Done as part of phase 1 — `LlmGateway` with per-tier `SemaphoreSlim` and configurable caps. ✅
3. **Add gateway metrics.** Slot wait time, in-flight count, queue depth. Slot-wait metric landed in phase 1. ✅
4. **Add bounded queue.** `MaxPendingPerTier` with fail-fast. (Phase 3.)
5. **Audit ct propagation.** Walk every existing LLM call site, confirm `ct` flows. Fix any that pass `default`/`None`. Make `ct` mandatory at the `ILlmClient` boundary so future violations cannot be introduced. (Phase 4.)
6. **Test cancellation contract.** Per-tier test that asserts cancellation aborts pending and in-flight calls promptly. Pending-side covered in phase 1; in-flight side completed in phase 4 alongside the audit.

A previously-planned phase to wrap rate-limit retry inside the gateway was attempted and reverted (see "Retry policy" and "Non-goals"). The OpenAI SDK's retry is more capable than what the gateway would build; we keep it enabled and let it run inside the gateway slot.

Phase 5 is the one that has follow-on work elsewhere in the codebase. Each violation is small but they have to all be fixed before the cancellation guarantee holds.

## Open questions

- **Cap defaults.** The 8/4/2 split above is a guess. Real workload will inform tuning. Should be config-overridable.
- **Cross-process coordination.** If multiple agent processes run against the same provider account, per-process caps undercount real concurrency. Out of scope for v1, but worth noting as a future concern (Redis-backed gateway? provider-side rate-limit tracking?).
- **Streaming responses.** Do any current call sites use streaming? If so, "in-flight" measurement and cancellation behave differently. Confirm during the audit pass.
- **Subagent message-bus boundary.** Subagent calls are dispatched via `IMessagePublisher`, which means `ct` does not naturally cross the bus. The receiving handler establishes a fresh `ct` from its own work-serializer slot, which is the right behavior — but worth confirming that no LLM calls inside a subagent handler accidentally fall back to `None` because the original ct didn't survive the boundary.
- **Pipeline-level retry telemetry.** The SDK does retries inside the gateway slot but doesn't surface retry events to our diagnostics. If centralized retry visibility becomes important, add a `PipelinePolicy`-derived listener that forwards retry telemetry into `HostDiagnostics`. Probably premature; record as a thought.
