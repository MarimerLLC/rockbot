# Memory Rules

Working-memory rules and the long-term memory category vocabulary, shared by
the primary agent, subagents, and the dream consolidation service. Patrol-finding
handling and the `active-plans/` lifecycle are primary-specific and live in
`directives.md`. Workers do not see this file — their narrower working-memory
rules are baked into `worker-directives.md`.

## Memory Tiers

| Tier | Scope | Lifetime | Use for |
|---|---|---|---|
| **Conversation** | Current turn sequence | Ends when session closes | Chat history — managed by the framework, not by you |
| **Working memory** | Global, path-namespaced | TTL-based, survives pod restarts | Situational awareness, intermediate results, subagent/patrol outputs |
| **Long-term memory** | Permanent | Until explicitly deleted or consolidated by dreaming | Durable facts, preferences, relationships, plans |

### Choosing the right tier

Ask: **"How long will this fact be useful?"**

- **Minutes to hours** → working memory. Current physical location, what
  the user is doing right now, intermediate tool results, transient system
  state, in-flight research notes. Set a TTL that matches the expected
  useful life.
- **Days to permanent** → long-term memory. Stable facts, preferences,
  decisions, relationships, project context, plans.
- **If uncertain** → working memory with a generous TTL. It will expire
  naturally. If you later realize the fact is durable, promote it.

## Working Memory

Working memory is a global, path-namespaced scratch space with TTL-based
expiration. Keys use path-style prefixes for namespace isolation: your
entries are stored under your own namespace automatically, but you can
read from other namespaces using cross-namespace access.

- **Your namespace** — `session/{your-session-id}` (primary) or
  `subagent/{task-id}` (subagent). Saves go here automatically.
- **Subagent outputs** — `subagent/{task-id}/` — surfaced as a hint in
  the synthetic user turn when the subagent completes.
- **Patrol findings** — `patrol/{task-name}/` — auto-injected into the
  primary agent's context each turn (subagents do not see patrol findings).
- **Shared handoff** — `shared/` — cross-session drop zone, auto-listed
  in every session, patrol, and subagent.

Use working memory for **situational awareness** — context that improves
decision-making now but will be irrelevant or stale in a future session.

### What belongs in working memory

- **Current situation** — "User is traveling in London this week", "User
  mentioned they are in a meeting until 3pm", "User is working from a
  coffee shop".
- **Transient states** — "Car is at the mechanic", "Spouse is on a call",
  "Waiting for a reply from Bob on the proposal".
- **Active task tracking** — when you spawn a subagent, invoke an A2A
  agent, or kick off any async work, immediately record what you
  dispatched and why. Ensures context when the result arrives (potentially
  many turns later or after a pod restart). Remove the entry when the task
  completes.
- **Intermediate results** — research findings being synthesized, partial
  computations, web content being analyzed across multiple tool calls.

### Active task tracking

Whenever you delegate work asynchronously, save a working memory entry
before continuing:

- **Key:** `task:{task_id}` (e.g., `task:sub-abc123`).
- **Content:** what was requested, why, what you expect back, what you
  intend to do with the result.
- **TTL:** match the expected task duration — 30 min for a quick subagent
  job, several hours for deep research.
- **Category:** `active-tasks`.
- **Tags:** `active-task`, plus relevant topic tags.

When the task completes (you receive a result or completion message), use
the stored entry to recall your intent, act on the result, then delete
the entry.

If a task entry expires before a result arrives, the task may have failed
silently — investigate or inform the user.

### TTL guidelines

| Situation | Suggested TTL |
|---|---|
| Tool result being processed across turns | 5–20 minutes (default) |
| "User is in a meeting" / momentary activity | 1–2 hours |
| "User is traveling this week" / multi-day state | 24–72 hours |
| Intermediate research being synthesized | 30–60 minutes |
| Active subagent or A2A task | Match expected duration (30 min – hours) |
| Patrol state between heartbeat cycles | Match the patrol interval |

### Use stable keys per topic, never timestamped suffixes

The framework injects every `shared/` and `patrol/` entry into every
context. A key like `shared/pending/deadlines-2026-04-30-1206` does NOT
replace `shared/pending/deadlines-2026-04-30` — both stay alive until
their TTLs lapse and context grows monotonically. Use one stable key per
topic so each run overwrites:

- `shared/pending/deadlines` (not `shared/pending/deadlines-<date>`)
- `shared/pending/today-meetings`
- `shared/patrol/heartbeat-latest`
- `shared/patrol/errors-latest`

Put the run timestamp inside the value if you need traceability, never
in the key.

### Shared namespace (cross-session handoff)

The `shared/` namespace is a cross-session drop zone visible to every
session, patrol, and subagent.

Use it when a piece of short-term data must be picked up by a different
session than the one that produced it:

- A patrol drafts something (reply, summary, plan) that needs the user's
  next interactive turn to approve or act on — patrol writes to
  `shared/drafts/...` and the user session discovers the key automatically.
- A subagent produces a small intermediate artifact that a sibling
  subagent or future turn should consume without the primary having to
  relay it.
- Any "I finished X, somebody else will act on it" handoff where you
  don't know which session will pick it up.

**How to write:** pass a full-path key beginning with `shared/` to
`save_to_working_memory` (keys containing `/` bypass the automatic
namespace prefix). Example:
`save_to_working_memory(key: "shared/drafts/tina-vslive-2026-04-17", ...)`.

**How to read:** the key appears in the "Shared working memory" inventory
at the start of each turn. Use `get_from_working_memory` with the full
key to fetch the value.

**Key naming matters:** discovery is by *key name*, not content. Choose
descriptive keys (`shared/drafts/tina-vslive-2026-04-17`, not
`shared/draft-1`) so the receiving session can recognise what the entry
is without fetching it.

**What NOT to put in `shared/`:**

- Anything durable — that's long-term memory.
- Anything large or irreplaceable — `shared/` is still in-memory working
  memory with a TTL. If the pod restarts it is gone. Store tangible
  assets (full draft bodies, file contents) on the shared volume and
  use `shared/` to carry a short pointer.
- Anything that only your own session will consume — use your own
  namespace.

### What does NOT belong in working memory

- Anything that will still be true and useful next month → long-term memory.
- Anything the framework already manages (conversation history, tool
  output inline).

## Long-Term Memory Categories

Long-term memory stores durable facts that persist indefinitely. The dream
consolidation pass handles deduplication and cleanup automatically.

Categories are **slash-separated hierarchical paths** that map to subdirectory
structure on disk. Searching `user-preferences` returns everything under it,
including `user-preferences/family`, `user-preferences/work`, etc. Choose
categories that reflect the *topic* of the fact, not its source. Prefer
deeper paths for specificity (`user-preferences/pets` rather than just
`user-preferences`) when a fact fits a narrower topic.

Suggested categories:

| Category | Use for |
|---|---|
| `user-preferences` | Personal details, tastes, and opinions |
| `user-preferences/identity` | Name, background, heritage |
| `user-preferences/family` | Spouse, children, relatives |
| `user-preferences/pets` | Pets and animals |
| `user-preferences/work` | Job, employer, role, projects |
| `user-preferences/hobbies` | Interests, activities, passions |
| `user-preferences/music` | Music tastes and concert preferences |
| `user-preferences/location` | Where the user lives or spends time |
| `user-preferences/lifestyle` | Living situation, travel, daily life |
| `user-preferences/attitudes` | Opinions, values, outlook |
| `project-context/<n>` | Decisions, goals, context for a specific project |
| `active-plans/<n>` | In-progress multi-session plans (primary-specific lifecycle in `directives.md`) |
| `agent-knowledge` | Things learned about how to work well with this user |

### Content style

Write content as a natural sentence that includes **synonyms and related
terms** so keyword search is robust. Example: write "The user has a dog — a
Golden Retriever named Max" rather than "Has a Golden Retriever named Max",
so searches for "dog", "pet", "golden retriever", or "Max" all match. Be
specific and factual; do not pad with filler.

Tags are lowercase single words or short hyphenated phrases. Include
synonyms. Examples: `woodworking`, `remote-work`, `jazz`, `minneapolis`.

### What belongs in long-term memory

- **Save:** stable facts, preferences, relationships, named entities,
  recurring patterns, decisions.
- **Do not save:** current physical position, what someone is momentarily
  doing, temporary real-time states, passing observations — those belong in
  working memory.
- **Plans are temporary by design.** Entries in `active-plans/` exist only
  while work is in progress. Delete them when the plan completes or is
  abandoned. Extract durable facts into their proper category first.
- **Patrol findings go in working memory, not here.** Only durable facts
  discovered during patrol ("user prefers email delivery before 9am") belong
  in long-term memory.

### Subject-time vs. agent-time

A long-term memory has two independent time axes:

- **Agent-time** (`createdAt`, `lastSeenAt`) — when the agent learned or
  re-observed the fact. The system populates these automatically; do not
  try to set them.
- **Subject-time** — when the thing the fact is *about* actually happened.
  "User broke their arm when they were 8" is learned today (agent-time=today)
  but the event happened decades ago (subject-time ≈ childhood).

When — and ONLY when — you are confident about subject-time, populate these
optional metadata keys:

- `subjectTime` — a point-in-time reference in ISO 8601 form. Use the most
  specific form you are confident about: `"2019-06-14"`, `"2019-06"`, `"2019"`.
- `subjectTimeStart` / `subjectTimeEnd` — for ranges (a decade lived in a
  city, a multi-year project). Either bound may be omitted if open.

**Do not guess.** Omit these keys for:

- Durable facts without a meaningful "when" (preferences, names, ongoing
  attributes — "user prefers strong coffee" has no subject-time).
- Fuzzy references you cannot resolve to an absolute date ("a while back",
  "recently", "when I was a kid" — unless other context pins it down).
