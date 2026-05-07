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
- Honor `Retry-After` on 429s, with exponential backoff as fallback.
- Cancellation propagates end-to-end: pending and in-flight calls abort when their `ct` fires.
- User-facing calls effectively preempt background calls without a priority queue.
- Centralized observability: token counts, latency, retry counts, queue depth, cost.
- Bounded queue depth: the system fails fast under sustained rate limiting rather than piling up indefinitely.

## Non-goals

- Priority queues / priority lanes. User-priority semantics are achieved via cancellation (see "User priority"). Lanes were considered and rejected as unneeded complexity.
- Replacing the SDK. The gateway wraps the SDK; it does not reimplement provider clients.
- Per-call routing across providers. Tier selection remains the caller's concern; the gateway only mediates calls within whatever tier the caller chose.
- Solving observation-framework parallelism by itself. The gateway is a prerequisite, not a replacement.

## Architecture

Every LLM call passes through the gateway. The gateway holds per-tier `SemaphoreSlim` instances and applies retry middleware on the way through.

```
   Caller (handler / dream phase / subagent)
        │  ILlmClient.GetResponseAsync(request, ct)
        ▼
   ┌─────────────────────────────────────────────────┐
   │  LlmGateway                                     │
   │                                                 │
   │   await _tierSemaphores[tier].WaitAsync(ct);    │
   │   try {                                         │
   │     // retry middleware:                        │
   │     //   on 429 -> honor Retry-After or         │
   │     //   exponential backoff (ct-aware sleep)   │
   │     //   max attempts capped                    │
   │     // metrics: tokens, latency, retries, $$    │
   │   } finally {                                   │
   │     _tierSemaphores[tier].Release();            │
   │   }                                             │
   └────────────────────┬────────────────────────────┘
                        │
                        ▼
                Provider SDK (with built-in retry disabled)
```

The gateway is the only direct consumer of the provider SDK. `ILlmClient` implementations that bypass it must be removed.

## Cancellation, not priority lanes

User-priority semantics emerge from cancellation. A priority queue was considered and rejected.

The reasoning: the work-serializer already cancels the dream when a user message arrives (`DreamService.DreamAsync` acquires via `TryAcquireForScheduledAsync`, which yields a CT that fires on preemption). When dream's `ct` fires:

- `SemaphoreSlim.WaitAsync(ct)` causes every dream LLM call currently waiting in the queue to throw `OperationCanceledException` and exit, freeing slots.
- In-flight LLM calls inside dream phases see `ct` cancelled and the SDK request aborts.
- The user-facing call's `WaitAsync` then succeeds promptly.

This gives the same outcome as priority lanes — user calls do not wait behind dream calls — without any of the complexity that priority brings: no ambient priority context, no envelope priority field on cross-bus calls, no subagent priority inheritance question, no priority-aware retry semantics.

The one behavioral difference: priority lanes would let dream continue running its non-contending calls during a brief user turn, whereas cancellation drops the dream's queued work entirely. Since the work-serializer already cancels the entire dream cycle on user preemption, this is not a regression — it matches the existing architecture.

## Retry policy

On 429:

1. If the response carries a `Retry-After` header (Anthropic and OpenAI both do), wait that long. The wait is `ct`-aware (`Task.Delay(retryAfter, ct)`).
2. If no `Retry-After`, fall back to exponential backoff: 1s, 2s, 4s, 8s, capped at some configurable maximum (16s suggested).
3. After a configurable maximum number of retries (default 5), surface the failure to the caller. The caller decides what to do.

The slot is held during the retry wait. Releasing the slot during the wait does not help: rate limits are per-tier, so any other call in that tier will hit the same limit. Holding the slot also keeps priority semantics simple: a retried call does not jump ahead of a newly-arrived call (or vice versa) because there is no priority to think about.

The provider SDK's built-in retry MUST be disabled. Gateway and SDK both retrying causes them to fight: the SDK retries silently, gateway sees success, then the same call hits the limit again. The gateway owns retry; the SDK does not.

Other error classes (5xx, network errors) get their own short retry policy (1 retry with brief backoff) for transient failures, but are not the primary concern. 401/403 and 4xx-other are non-retryable and surface immediately.

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

1. **Wrap, don't replace.** The gateway is a thin layer in front of `ILlmClient` (or whatever the existing abstraction is). Existing call sites already go through that abstraction; the gateway is plumbed in behind it without touching call sites.
2. **Disable SDK retry.** Configure the underlying SDK clients to not retry. Confirm via test.
3. **Add per-tier semaphores + retry middleware.** Honor `Retry-After`, exponential fallback, ct-aware sleeps.
4. **Audit ct propagation.** Walk every existing LLM call site, confirm ct flows. Fix any that pass `None`.
5. **Add metrics.** Wire tokens / latency / retries through the existing telemetry pipeline.
6. **Add bounded queue.** Start with fail-fast.
7. **Test cancellation contract.** Per-tier test that asserts cancellation aborts pending and in-flight calls promptly.

Step 4 is the one that has follow-on work elsewhere in the codebase. Each violation is small but they have to all be fixed before the cancellation guarantee holds.

## Open questions

- **Cap defaults.** The 8/4/2 split above is a guess. Real workload will inform tuning. Should be config-overridable.
- **Cross-process coordination.** If multiple agent processes run against the same provider account, per-process caps undercount real concurrency. Out of scope for v1, but worth noting as a future concern (Redis-backed gateway? provider-side rate-limit tracking?).
- **Streaming responses.** Do any current call sites use streaming? If so, "in-flight" measurement and cancellation behave differently. Confirm during the audit pass.
- **Subagent message-bus boundary.** Subagent calls are dispatched via `IMessagePublisher`, which means `ct` does not naturally cross the bus. The receiving handler establishes a fresh `ct` from its own work-serializer slot, which is the right behavior — but worth confirming that no LLM calls inside a subagent handler accidentally fall back to `None` because the original ct didn't survive the boundary.
- **Retry budget per call vs. per cycle.** Should there be a global budget on retries-per-dream-cycle so that a degraded provider doesn't burn the whole cycle on a single phase's retries? Probably premature, but record as a thought.
