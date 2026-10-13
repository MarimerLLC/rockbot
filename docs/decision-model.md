# Decision model

RockBot makes many small judgements about text: did the user ask for this change, is this message
a correction, is this trailing paragraph a throwaway offer, which model tier should answer.
These used to be regexes and keyword lists, and each failed the same way: a phrasing the list had
no words for got the wrong answer. In 0.16.14-preview.2 the action gate's verb list had no "mark",
so "mark them complete now" was refused and the agent could not complete todos.

A **decision model** answers bounded questions with probabilities instead of prose. RockBot uses
`Microsoft-Decision-1` on Microsoft Foundry (about 300–650 ms a call, $0.042 per million input
tokens, output free) behind one swappable interface. **Every use keeps its deterministic check as
the first pass** and asks the model only about what that check cannot settle. When the model is
off, slow, rate limited or unreadable, the deterministic answer stands.

## The service

`IDecisionService` (`RockBot.Llm.Abstractions`):

```csharp
bool IsEnabled(string purpose);                       // configured and this use not switched off
double ThresholdFor(string purpose, double fallback); // per-use override
Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken ct);
```

A `DecisionRequest` has a `Purpose` (a `DecisionPurposes` value), a `State` (text, or an object
serialized to JSON) and named questions:

| Question | Answer | Use for |
|---|---|---|
| `NoulQuestion(instructions, whenTrue?, whenFalse?)` | `Probability` of yes, 0–1 | gates and filters |
| `ChoiceQuestion(instructions, options)` | `Choice` plus `Probabilities` per option | routing, classification |
| `ScoreQuestion(instructions, levels)` | `Score` (probability-weighted level index) plus per-level probabilities | ranking, grading |

Several questions about the same state go in one request. `DecideAsync` returns null unless every
question was answered.

Implementations: `FoundryDecisionService` (`RockBot.Host`, the systemone API) and
`NullDecisionService` (never enabled). To swap in another provider, register your own
`IDecisionService` before `AddRockBotHost`; the host registers its default with `TryAdd`.

Observed response shapes (2026-10-13):

```json
{"model":"microsoft-decision-1",
 "answers":{
   "is_question":{"noul":0.958,"type":"noul"},
   "tier":{"choice":"high","confidence":0.855,"probabilities":{"low":0.002,"balanced":0.095,"high":0.903},"type":"choice"},
   "severity":{"score":1.913,"confidence":0.183,"legend":{"0":"Not urgent","1":"Soon","2":"Today","3":"Immediately"},
               "probabilities":{"0":0.006,"1":0.342,"2":0.387,"3":0.266},"type":"score"}},
 "usage":{"input_tokens":118,"output_tokens":3}}
```

## Uses

| Purpose | Deterministic first pass | Model asked when | Model unavailable | Default threshold |
|---|---|---|---|---|
| `ActionGate` | Regex verb list reads the user message as instruction or information-only | The regex would refuse an external change. One question per turn per tool, shared with subagents | `AllowOnDecisionFailure` (default allow) | 0.5 |
| `Correction` | No prior reply or an acknowledgement → no; "that's wrong", "you were wrong", "that didn't work" → yes; no cue word → no | A weak cue (no, not, actually, instead, should have…). Runs off the reply path | The pre-model regex | 0.7 |
| `CompletionCheck` | `CompletionEvalTriggers.Decide` regexes | They find nothing on a user turn that stopped on its own (not small talk, relays or subagents). One call, three questions | Evaluator skipped, as before | 0.5 |
| `TrailingOffer` | Regex finds a trailing "Should I… / Let me know if…" paragraph | A candidate was found | Stripped, as before | 0.5 |
| `MemoryDuplicate` | Similarity ≥ threshold and no missing names or numbers → reinforce, drop new text | Before reinforcing | Reinforced, as before | 0.5 |
| `MemoryContradiction` | Keyword valence plus overlap picks candidates | There are candidates. One call covers up to 10 | Keyword verdict | 0.5 |
| `TierRouting` | Keyword selector score bands, floors and guards | The score band decided and the score is within `TierRoutingMargin` (0.08) of a ceiling | Keyword tier | 0.6 confidence; moves at most one tier |

What each use asks:

- **ActionGate** — "Did user_message ask the assistant to make this change, ask for work that
  requires it, or agree to it after agent_message_before_it proposed it?" State: the user's own
  words, the agent's previous reply, the proposed tool call. Only user turns attach the message; a
  subagent's own task text is never judged as the user's words.
- **Correction** — "Is user_message telling the assistant that agent_reply was wrong, incomplete,
  misunderstood the request, or did not work?" A correction writes a `Correction` feedback entry,
  which marks the session at risk for the dream's skill rewrites.
- **CompletionCheck** — `promises_unfinished_work`, `claims_change_no_call_made` and
  `denies_capability`, over the user message, the reply and the turn's tool calls (with outcome
  and whether each changed state). A yes maps onto `ClaimedChange`, `PromiseNoAction` (only when no
  substantive call ran) or `Pattern`, so the existing re-prompt guidance applies.
- **TrailingOffer** — "Can trailing_paragraph be removed without losing anything the user needs?"
  Questions the user must answer ("Should I book 3pm or 4pm?") are kept.
- **MemoryDuplicate** — "Does existing_memory already state everything new_memory states, with
  nothing changed, negated, updated or contradicted?" If not, the new entry is saved alongside.
- **MemoryContradiction** — one `contradicts_i` question per candidate: "Does new_memory contradict
  existing_i, so that both cannot be true or followed at once?" Only confirmed candidates are
  superseded.
- **TierRouting** — a choice between `low`, `balanced` and `high`, with descriptions. The routing
  log records rule `decision-model` when the model changed the tier.

Also fixed while here, with no model involved: the dream's at-risk detection read session-summary
feedback with `Contains("poor") || Contains("fair")`, which matched "unfair", "fairly" and tool
names. It now reads the evaluator's `overallQuality` field.

### Probe results (2026-10-13, deployment `decision-1`)

| Use | Cases | Result |
|---|---|---|
| ActionGate | 10 (todo completions, offers accepted; #685 context-only, questions) | 10/10. Requests ≥ 0.75, non-requests ≤ 0.007 |
| Correction | 6 | 6/6 at threshold 0.7. Corrections ≥ 0.999; "I have no idea what time it starts" 0.651 |
| TrailingOffer | 4 | 4/4. Needed questions ≤ 0.011, throwaway offers ≥ 0.996 |
| CompletionCheck | 5 replies × 3 questions | 15/15. Yes ≥ 0.98, no ≤ 0.165 |
| MemoryDuplicate | 3 | 3/3. Negation and new value 0.001, restatement 1.000 |
| MemoryContradiction | 2 | 2/2. "Use X, never Y" vs "always use X" 0.085; vs "never use X" 0.998 |
| TierRouting | 3 | 3/3. Each ≥ 0.994 |

## Configuration

Top-level `DecisionModel` section (env `DecisionModel__…`). Nothing is asked until `Endpoint`,
`ApiKey` and `Deployment` are all set.

| Key | Default | Meaning |
|---|---|---|
| `Endpoint` | — | Foundry resource endpoint, e.g. `https://my-resource.services.ai.azure.com` |
| `ApiKey` | — | Foundry key (`api-key` header) |
| `Deployment` | — | Deployment name of `Microsoft-Decision-1` |
| `Path` | `/providers/microsoft/v1/systemone` | Decision API path |
| `TimeoutSeconds` | `5` | Per-call timeout; on timeout the caller keeps its deterministic answer |
| `Uses:<Purpose>:Enabled` | `true` | Switch one use off, e.g. `DecisionModel__Uses__TierRouting__Enabled=false` |
| `Uses:<Purpose>:Threshold` | per use (above) | Override one use's threshold |
| `TierRoutingMargin` | `0.08` | How close to a tier ceiling the keyword score must be before routing asks |

Plus `AgentHost:ConsequentialActionGate:AllowOnDecisionFailure` (default `true`) for the gate.

Helm: `agent.decisionModel.{endpoint, deployment, timeoutSeconds, allowOnGateFailure, uses}` and
`secrets.decisionModel.apiKey`. When `apiKey` is empty the secret uses
`secrets.llm.balanced.apiKey`, which is right when one Foundry account serves the LLM tiers and
Decision-1. **If the balanced tier moves to another provider, set `secrets.decisionModel.apiKey`**:
otherwise every call fails with 401 and every use falls back to its deterministic answer (for the
gate, with `allowOnGateFailure` on, every gated change runs).

```yaml
agent:
  decisionModel:
    endpoint: https://my-resource.services.ai.azure.com
    deployment: decision-1
    uses:
      TierRouting: { enabled: false }
      Correction: { threshold: 0.8 }
```

### Deployment and capacity

```bash
az cognitiveservices account deployment create -n <account> -g <resource-group> \
  --deployment-name decision-1 --model-name Microsoft-Decision-1 --model-format Microsoft \
  --model-version 1 --sku-name DataZoneStandard --sku-capacity 20
```

Re-running with a new `--sku-capacity` resizes in place. **Capacity N allows N requests per
minute**, shared by every use. A busy turn can make several calls (tier routing, completion check,
gate, offer, correction), so watch `rockbot.decision.calls{outcome="rate-limited"}` and raise the
capacity if it climbs. Billing is per token, so headroom doesn't add cost.

## Observability

- `rockbot.decision.calls`: every call, tagged `rockbot.decision.purpose` and
  `rockbot.decision.outcome` (`ok`, `rate-limited`, `http-error`, `timeout`, `unreadable`,
  `error`).
- `rockbot.decision.duration` (ms): tagged the same way.
- Each use logs its verdict at Information, starting `Decision model:`. Failures log at Warning
  with the response body (truncated).

## Adding a use

1. Add a `DecisionPurposes` constant (PascalCase, since it becomes an env var segment).
2. Keep the deterministic check. Decide which of its verdicts are certain and which to ask about.
3. Build the request next to the caller, with the question wording in one place, and write
   `WhenTrue` / `WhenFalse` criteria for borderline cases.
4. On null, return the deterministic answer.
5. Probe 5–10 labeled examples against the real model before picking a threshold, and record the
   numbers in this file.
