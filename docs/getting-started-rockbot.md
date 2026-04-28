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
7. Memory rules that encourage a durable "theory of the user" and "theory of self"
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

These belong in long-term memory, typically under `user-preferences/...` or `agent-knowledge`.

### Decide who the agent is

There are two different "names" in RockBot:

- **Conversational identity** - what the agent calls itself in chat. This is mostly driven by `soul.md`.
- **Technical identity** - the deployment/runtime `AgentIdentity` used for routing on the message bus.

For most onboarding, you only need to change the **conversational identity** by editing `soul.md`. If you want the agent to present itself as "Aki", "Roxy", or something other than "RockBot", put that in `soul.md`. Changing the runtime routing identity is a deeper deployment change and is not usually required just to get started.

### Customize `soul.md`, `directives.md`, and `style.md`

The profile files on the agent data volume shape how the agent behaves:

| File | Use it for |
|---|---|
| `soul.md` | Core identity, values, role, boundaries, and overall personality |
| `directives.md` | Operational instructions, workflows, priorities, and what "good work" looks like |
| `style.md` | Optional voice and tone polish |
| `memory-rules.md` | What to remember, what not to remember, and how memory should evolve |

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

For example, if you want calendar access, add an MCP server such as [`calendar-mcp`](https://github.com/MarimerLLC/calendar-mcp) to `mcp.json`:

```json
{
  "mcpServers": {
    "calendar-mcp": {
      "type": "sse",
      "url": "http://host.docker.internal:3000/"
    }
  }
}
```

After the file updates, the agent hot-reloads the MCP configuration. Then, in chat, have the agent confirm what it sees:

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

## 4. Teach it how to build memory on purpose

The best RockBot agents do not just accumulate random memories. They deliberately maintain:

- a **theory of the user**
- a **theory of self**

### Theory of the user

This is the agent's evolving model of your preferences, patterns, relationships, projects, and context.

Useful categories include:

- `user-preferences/identity`
- `user-preferences/work`
- `user-preferences/location`
- `user-preferences/lifestyle`
- `user-preferences/attitudes`
- `project-context/<project-name>`
- `agent-knowledge`

Ask for it explicitly at the start:

```text
Build and maintain an evolving theory of me. Save durable facts, preferences, recurring patterns, active projects, and relationship context in memory so you become more useful over time.
```

### Theory of self

RockBot already has a good place for this: the `agent-identity/...` memory categories. These are for the mutable self-model that grows through experience without replacing the immutable `soul.md`.

Useful categories include:

- `agent-identity/mission`
- `agent-identity/goals`
- `agent-identity/projects`
- `agent-identity/capabilities`
- `agent-identity/self-model`

Ask for it explicitly:

```text
Build and maintain an evolving theory of yourself: your mission, strengths, limitations, active responsibilities, and the kind of agent you are becoming for me. Keep that in memory without changing your core soul.
```

### Preserve the time dimension

Do not treat memory like a timeless blob. RockBot's memory model already tracks `CreatedAt`, `LastSeenAt`, and `UpdatedAt`, and can also preserve subject-time metadata when known. Use that.

Practical guidance:

1. Save durable facts as durable facts
2. Save active plans in `active-plans/...`
3. Save evolving self-model entries under `agent-identity/...`
4. When a meaningful shift happens, update the relevant memory instead of stuffing everything into `soul.md`
5. When the underlying fact refers to a real period in your life, preserve that time context if known

This is how the agent keeps a usable history instead of endlessly rewriting a flat summary.

## 5. Tune `memory-rules.md`

If you want consistent long-term behavior, do not rely only on one chat instruction. Put the policy in `memory-rules.md`.

A good `memory-rules.md` usually tells the agent:

- what counts as durable user context
- what should stay in working memory instead
- which categories to prefer
- when to create or update `agent-identity/...` entries
- how aggressively to save preferences
- how to handle active plans and completed plans

Strong additions for a personal agent often include rules like:

- remember names, relationships, preferences, and ongoing responsibilities
- maintain a compact theory of the user
- maintain a compact theory of self
- preserve time context for important events and life changes
- avoid saving noisy one-off tool output as long-term memory

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
- `mcp.json` if it lacks key systems
- scheduled tasks if it is not being proactive enough

## 7. A simple onboarding sequence that works well

If you want a concrete sequence, this is a good starting point:

1. Finish the [Docker Desktop guide](getting-started-docker-desktop)
2. Edit `soul.md` so the agent has the right role and name
3. Add `style.md` if tone matters
4. Tell the agent your name, timezone, and main areas of life or work it should care about
5. Connect 1-3 MCP servers you will use every week
6. Ask it to test each server with one real task
7. Create 2-4 scheduled tasks that produce proactive value
8. Tell it to maintain a theory of the user and a theory of self in memory
9. Tune `memory-rules.md` so that behavior is durable and repeatable
10. Revisit the profile and memory after a few days of real use

## Related docs

- [Getting started with Docker Desktop](getting-started-docker-desktop)
- [Agent host](agent-host)
- [Memory](memory)
- [Tools](tools)
- [Dream service](dream-service)
