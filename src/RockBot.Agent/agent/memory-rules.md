# Memory Rules

These rules govern what to store, where to store it, and when to discard it
across all three memory tiers.

## Memory tiers

| Tier | Scope | Lifetime | Use for |
|---|---|---|---|
| **Conversation** | Current turn sequence | Ends when session closes | Chat history — managed by the framework, not by you |
| **Working memory** | Global, path-namespaced | TTL-based, survives pod restarts | Situational awareness, intermediate results, subagent/patrol outputs |
| **Long-term memory** | Permanent | Until explicitly deleted or consolidated by dreaming | Durable facts, preferences, relationships, plans |

### Choosing the right tier

Ask: **"How long will this fact be useful?"**

- **Minutes to hours** → working memory. Current physical location, what the user
  is doing right now, intermediate tool results, transient system state, in-flight
  research notes. Set a TTL that matches the expected useful life.
- **Days to permanent** → long-term memory. Stable facts, preferences, decisions,
  relationships, project context, plans.
- **If uncertain** → working memory with a generous TTL. It will expire naturally.
  If you later realize the fact is durable, promote it to long-term memory.

## Working memory

Working memory is a global, path-namespaced scratch space with TTL-based expiration.
Keys use path-style prefixes to provide namespace isolation: your entries are stored
under your own namespace automatically, but you can read from other namespaces
(subagents, patrol tasks) using cross-namespace access.

**Your namespace**: `session/{your-session-id}` — all saves go here automatically.
**Subagent outputs**: `subagent/{task-id}/` — surfaced as a hint in the synthetic user turn when the subagent completes.
**Patrol findings**: `patrol/{task-name}/` — automatically injected into your context each turn (see "Patrol findings" section below). Use `get_from_working_memory` with the full key to read them.

Use working memory for **situational awareness** — context that improves decision-making
now but will be irrelevant or stale in a future session.

### What belongs in working memory

- **Current situation**: "User is traveling in London this week", "User mentioned
  they are in a meeting until 3pm", "User is working from a coffee shop"
- **Transient states**: "Car is at the mechanic", "Spouse is on a call",
  "Waiting for a reply from Bob on the proposal"
- **Active task tracking**: When you spawn a subagent, invoke an A2A agent, or
  kick off any async work, immediately record what you dispatched and why. This
  ensures you have context when the result arrives — potentially many turns later
  or after a pod restart. Remove the entry when the task completes.
- **Intermediate results**: Research findings being synthesized, partial
  computations, web content being analyzed across multiple tool calls
- **Patrol state and findings**: What was checked, what was found, what the patrol
  decided — stored in the patrol's own namespace (`patrol/{task-name}/`) with a TTL
  matching the patrol interval so findings are visible to the primary agent until
  the next run overwrites them

### Active task tracking

Whenever you delegate work asynchronously, save a working memory entry before
continuing:

- **Key**: `task:{task_id}` (e.g., `task:sub-abc123`, `task:a2a-research-xyz`)
- **Content**: What was requested, why it was requested, what you expect back,
  and what you intend to do with the result
- **TTL**: Match the expected task duration — 30 minutes for a quick subagent
  job, several hours for deep research, etc.
- **Category**: `active-tasks`
- **Tags**: `active-task`, plus relevant topic tags

Example:
```
key: task:sub-a1b2c3
content: Spawned subagent to research Kubernetes KEDA scaling patterns for
  the RockBot ephemeral container design. User asked about autoscaling
  options. Will summarize findings and recommend an approach when complete.
category: active-tasks
tags: active-task, kubernetes, keda, subagent
ttl: 60 minutes
```

When the task completes (you receive a result or completion message), use the
stored entry to recall your intent, act on the result accordingly, then delete
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
| Active subagent or A2A task | Match expected task duration (30 min – several hours) |
| Patrol state between heartbeat cycles | Match the patrol interval |

### Patrol findings

When patrol tasks run, they store their state and findings in working memory under
`patrol/{task-name}/`. At the start of every user session turn, the framework
automatically injects a summary of those entries into your context — you don't need
to call `list_working_memory` yourself. The entries are listed under
**"Patrol findings in working memory"** in your system context.

To act on patrol findings:
1. Read the injected summary to see what exists and how long until expiry
2. Call `get_from_working_memory("patrol/{task-name}/your-key")` to load the detail
3. Present or act on the findings as appropriate
4. The entries expire automatically when their TTL lapses — typically at the next patrol run

**Patrol tasks** store findings using `save_to_working_memory` with a TTL that spans at
least one full patrol cycle (e.g. 5 hours for an hourly patrol), so entries are available
to the primary agent between runs. Each run overwrites the previous entries.

### What does NOT belong in working memory

- Anything that will still be true and useful next month → long-term memory
- Anything the framework already manages (conversation history, tool output inline)

## Long-term memory

Long-term memory stores durable facts that persist indefinitely. The dream
consolidation pass handles deduplication and cleanup automatically.

### Categories

Categories are **slash-separated hierarchical paths** that map directly to
subdirectory structure on disk:

- Related memories are physically grouped and retrieved together by searching a parent prefix
- Searching `user-preferences` returns everything under it, including `user-preferences/family`, `user-preferences/work`, etc.
- Choose categories that reflect the *topic* of the fact, not its source
- Prefer deeper paths for specificity (`user-preferences/pets` rather than just `user-preferences`) when a fact clearly belongs to a narrower topic
- Invent subcategories whenever a topic warrants its own grouping

**Suggested categories:**

| Category | Use for |
|---|---|
| `user-preferences` | Personal details, tastes, and opinions |
| `user-preferences/identity` | Name, background, heritage |
| `user-preferences/family` | Spouse, children, relatives, siblings |
| `user-preferences/pets` | Pets and animals |
| `user-preferences/work` | Job, employer, role, projects |
| `user-preferences/hobbies` | Interests, activities, passions |
| `user-preferences/music` | Music tastes and concert preferences |
| `user-preferences/location` | Where the user lives or spends time |
| `user-preferences/lifestyle` | Living situation, travel, daily life |
| `user-preferences/attitudes` | Opinions, values, outlook on life |
| `project-context/<n>` | Decisions, goals, and context for a specific project |
| `active-plans/<n>` | In-progress multi-session task plans (see directives for lifecycle) |
| `agent-knowledge` | Things learned about how to work well with this user |

### Content style

- Write content as a natural sentence that includes **synonyms and related terms** so keyword search is robust
- Example: write "The user has a dog — a Golden Retriever named Max" rather than "Has a Golden Retriever named Max", so searches for "dog", "pet", "golden retriever", or "Max" all match
- Be specific and factual; do not pad with filler

### Tags

- Lowercase single words or short hyphenated phrases
- Include synonyms and related terms
- Examples: `woodworking`, `remote-work`, `jazz`, `minneapolis`, `home-lab`

### What belongs in long-term memory

- **Save**: stable facts, preferences, relationships, named entities, recurring patterns, decisions
- **Do not save**: current physical position, what someone is momentarily doing, temporary real-time states, passing observations with no lasting significance — these belong in working memory instead
- **Plans are temporary by design**: entries in `active-plans/` exist only while work is in progress. Delete them when the plan is complete or abandoned. Extract any durable facts (decisions made, preferences discovered) into their proper category before deleting the plan.
- **Patrol findings go in working memory, not here**: patrol results are stored in `patrol/{task-name}/` working memory entries with a long TTL. Only durable facts discovered during patrol (e.g. "user prefers email delivery before 9am") belong in long-term memory.
