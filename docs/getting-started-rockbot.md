---
title: Getting started with RockBot
nav_order: 3
---

# Getting started with RockBot

This guide starts where [Getting started with Docker Desktop](getting-started-docker-desktop) ends. Step 1 is getting RockBot running. Step 2 is turning it into **your** agent: giving it the right identity, tools, habits, and memory behavior so it becomes genuinely useful over time.

If you only do one thing after the Docker guide, do this: **customize the profile, connect the right MCP servers, and give the agent recurring jobs.** That is the difference between "a chat UI with an LLM behind it" and "an agent that gets better at helping you."

## What "successfully onboarded" looks like

A newly useful RockBot usually has all of the following:

1. A `soul.md` that matches the role you want it to play
2. A `style.md` if tone matters
3. Clear knowledge of who **you** are and what to call you
4. Clear knowledge of who **it** is and what to call itself
5. MCP servers for the systems you actually use
6. A few scheduled tasks that create proactive value
7. The observation framework actively maintaining a "theory of the user" and "theory of self"
8. A short feedback loop where you keep tuning its profile and memory

## 1. Make the agent yours

### Tell it who you are

Do this early. A personal agent that does not know your name, timezone, role, and preferences will feel generic.

Good first-turn examples:

```text
My name is Rocky. Please call me Rocky.
```

```text
Remember that I am in America/Chicago and I prefer concise, direct answers.
```

```text
Remember that I use RockBot mainly for communications, scheduling, technical work, and research.
```

These belong in long-term memory.

### Decide who the agent is

There are really **three** related identity surfaces in RockBot:

- **Runtime identity** — the deployment/runtime `AgentIdentity` used for routing on the message bus
- **Display name** — the mutable name shown in chat and user-facing UI
- **Persona** — the broader self-description and role conveyed by `soul.md`

The important correction is that the **display name is not only driven by `soul.md`**. RockBot supports a runtime-changeable display name backed by `agent-name.md`, and that name can be changed by the agent itself through the introspection MCP tools. The host hot-reloads it, publishes an `AgentNameChanged` notification, and frontends such as the Blazor UI update to show the new name.

So in practice:

- Use **`set_agent_name`** / `agent-name.md` when you want to rename how the agent appears in chat and the UI
- Use **`soul.md`** when you want to change who the agent is, how it describes itself, and the role or character it should inhabit
- Change **`AgentIdentity`** only when you actually need a different routing identity for deployment or messaging

If you want the agent to present itself as "Aki", "Roxy", or something other than "RockBot", do that through the display-name path. Treat `soul.md` as persona and role, not as the authoritative place to rename the agent.

### Customize `soul.md`, `directives.md`, and `style.md`

The profile files on the agent data volume shape how the agent behaves:

| File | Use it for |
|---|---|
| `soul.md` | Core identity, values, role, boundaries, and overall personality |
| `directives.md` | Operational instructions, workflows, priorities, and what "good work" looks like |
| `style.md` | Optional voice and tone polish |
| `memory-rules.md` | What to remember, what not to remember, and how memory should evolve |
| `agent-name.md` | Optional mutable display name shown in chat and UI |

Good onboarding practice:

1. Put the stable persona in `soul.md`
2. Put deployment- or user-specific operating rules in `directives.md`
3. Put tone only in `style.md`

For example, a strong `soul.md` usually answers:

- Who is this agent to the user?
- What domains does it proactively care about?
- How direct or gentle should it be?
- What are its boundaries?

And `style.md` usually answers:

- Should it be brief or expansive?
- Formal or conversational?
- Should it use bullets heavily, or mostly prose?

If you are running with Docker Compose, these files live on the `agent-data` volume and hot-reload when changed. See [Getting started with Docker Desktop](getting-started-docker-desktop#customizing-the-agent) for the file locations.

## 2. Connect the systems that make it useful

RockBot becomes much more valuable once it can reach your real tools through MCP.

Typical high-value servers:

- Calendar
- Email
- Contacts
- Task systems
- GitHub
- Files or notes
- Internal line-of-business tools

The normal user-facing path is to ask the agent to connect the server for you. For example:

```text
Connect the calendar MCP server for me so you can read my calendar. Use this SSE endpoint: http://host.docker.internal:3000/
```

Under the hood, RockBot can maintain the MCP configuration through chat. If you prefer direct control, you can also edit `mcp.json` yourself. For example, a calendar server entry looks like:

```json
{
  "mcpServers": {
    "calendar-mcp": {
      "type": "sse",
      "url": "http://host.docker.internal:3000/",
      "attachments": {
        "outbound": { "paramPaths": ["attachments[*]"] },
        "inbound":  { "tools": ["get_email_attachment"] }
      }
    }
  }
}
```

The optional `attachments` block opts the server into the bridge's [attachment passthrough](tools.md#attachment-passthrough): the agent passes `{ path: "/rockbot/shared/attachments/<file>" }` instead of base64, and the bridge handles upload/download against the server's REST endpoints behind the scenes.

After the configuration updates, the agent hot-reloads the MCP setup. Then, in chat, have the agent confirm what it sees:

```text
List the MCP services you currently have available and summarize what each one is for.
```

Good onboarding habit: after connecting a server, immediately ask the agent to use it for one real task so it can form the right skills and expectations.

Examples:

```text
Check my calendar for today and summarize anything important.
```

```text
Look at my next 7 days of meetings and tell me where I look overloaded.
```

## 3. Give it recurring jobs

A personal agent feels much better when it does useful work without waiting to be asked. Scheduled tasks are one of the fastest ways to get there.

Good starter tasks:

- Morning communications briefing
- Daily calendar review
- End-of-day follow-up sweep
- Weekly planning summary
- Project or inbox patrols

Example requests you can paste into chat:

```text
Create a weekday 7:30 AM scheduled task called morning-briefing that checks my calendar, recent messages, and any urgent follow-ups, then gives me a concise morning briefing.
```

```text
Create a weekday 4:30 PM scheduled task called end-of-day-sweep that reviews unfinished threads, tomorrow's meetings, and anything I should prepare tonight.
```

```text
List my scheduled tasks and tell me which ones seem redundant or missing.
```

The scheduling tools are `schedule_task`, `list_scheduled_tasks`, and `cancel_scheduled_task`. See [Tools](tools#scheduling-tools-rockbottoolsscheduling) for the cron details.

### Confirm the background cadences

In addition to user-visible scheduled tasks, RockBot also has two important background rhythms you should confirm early:

- **Dream cycle** — the background reflection and consolidation pass
- **Heartbeat patrol** — the recurring proactive patrol

These are configured in the agent config with cron strings:

- `Dream:CronSchedule`
- `HeartbeatPatrol:CronExpression`

The current defaults are:

- Dream: `0 */12 * * *` — every 12 hours
- Heartbeat patrol: `0 */6 * * *`

Practical guidance:

- Run the **dream** about **1-2 times per day**
- Run the **heartbeat patrol** as often as is useful for the user
- Remember that more frequent patrols can noticeably increase LLM usage cost

For many users, a good onboarding step is simply to ask:

```text
Check the current dream and heartbeat patrol schedules and tell me whether they seem right for how I want to use you.
```

If you want to change them, update the config values with the cron schedules you want.

## 4. Theory of the user and theory of self

The best RockBot agents do not just accumulate random memories. They deliberately maintain:

- a **theory of the user** — an evolving structured model of you: what you focus on, patterns observed, where the agent is still uncertain
- a **theory of self** — the agent's evolving structured self-model that grows through experience without replacing the immutable `soul.md`

These artifacts are produced and maintained automatically by the **observation framework**, which runs as a phase of the dream cycle. Each dream, the framework reads recent conversation history, extracts evidence-grounded observations using a cheap LLM tier, clusters them into candidates, and promotes candidates that have been reinforced across multiple distinct conversations into the theory documents. Stale entries that are not reinforced age out.

The output is a deterministically regenerated `theory-of-self.md` and `theory-of-user.md` in the agent profile, with structured entries showing reinforcement counts, dates, and representative quotes. They are loaded into agent context like any other profile file.

You do not need to teach the agent to maintain these via prompts — the framework does it. If you previously seeded these files by hand or via the prompt patterns from earlier guides, they are imported once on first run and then maintained by the framework from there forward.

For the design and configuration knobs (promotion thresholds, aging windows, snapshot retention), see [`design/observation-framework.md`](https://github.com/MarimerLLC/rockbot/blob/main/design/observation-framework.md) in the repo.

### Preserve the time dimension

Do not treat memory like a timeless blob. RockBot's memory model already tracks `CreatedAt`, `LastSeenAt`, and `UpdatedAt`, and can also preserve subject-time metadata when known. Use that.

Practical guidance:

1. Save durable facts as durable facts
2. Save in-progress multi-session work as plans the agent can resume later
3. Let the agent keep an evolving self-model separate from `soul.md`
4. When a meaningful shift happens, update the relevant memory instead of stuffing everything into `soul.md`
5. When the underlying fact refers to a real period in your life, preserve that time context if known

This is how the agent keeps a usable history instead of endlessly rewriting a flat summary.

## 5. Tune how memory works

If you want consistent long-term behavior, do not rely only on one chat instruction. The important thing is to get the policy into the agent's durable configuration.

In today's RockBot setup, `memory-rules.md` is part of the agent profile rather than the shared file area the agent normally writes to. So the practical path is:

1. tell the agent how you want memory to work
2. let it help draft or refine the policy
3. update `memory-rules.md` yourself when you want that policy locked in

A good memory-rules policy usually tells the agent:

- what counts as durable user context
- what should stay in working memory instead
- when to create or update its self-model
- how aggressively to save preferences
- how to handle active plans and completed plans

Strong additions for a personal agent often include rules like:

- remember names, relationships, preferences, and ongoing responsibilities
- preserve time context for important events and life changes
- avoid saving noisy one-off tool output as long-term memory

(The "theory of the user" and "theory of self" artifacts are now maintained automatically by the observation framework — no rule needed.)

## 6. Run a deliberate first-week feedback loop

Most of the magic happens in the first few days of tuning.

After a few real conversations, ask:

```text
What do you currently believe about me that is important for helping me well?
```

```text
What do you currently believe about yourself, your role, and your strengths and limitations?
```

```text
What scheduled tasks, MCP tools, or memory rules would make you noticeably more helpful?
```

Then adjust:

- `soul.md` if the role feels wrong
- `style.md` if the tone feels wrong
- `directives.md` if the workflow is wrong
- `memory-rules.md` if it is remembering the wrong things
- the agent's MCP setup if it lacks key systems
- scheduled tasks if it is not being proactive enough
- the dream or heartbeat patrol cadence if background behavior feels off

## 7. A simple onboarding sequence that works well

If you want a concrete sequence, this is a good starting point:

1. Finish the [Docker Desktop guide](getting-started-docker-desktop)
2. Set the display name you want the agent to use in chat and the UI
3. Add `style.md` if tone matters
4. Tell the agent your name, timezone, and main areas of life or work it should care about
5. Ask the agent to connect 1-3 MCP servers you will use every week
6. Ask it to test each server with one real task
7. Create 2-4 scheduled tasks that produce proactive value
8. Confirm the dream and heartbeat patrol schedules are appropriate
9. Confirm the observation framework is enabled (it is by default) so theory-of-user and theory-of-self build over time
10. Tune the agent's memory rules so that behavior is durable and repeatable
11. Revisit the profile and memory after a few days of real use

## Related docs

- [Getting started with Docker Desktop](getting-started-docker-desktop)
- [Agent host](agent-host)
- [Memory](memory)
- [Tools](tools)
- [Dream service](dream-service)
