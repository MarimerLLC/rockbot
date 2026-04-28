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
      "url": "http://host.docker.internal:3000/"
    }
  }
}
```

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

## 4. Teach it how to build memory on purpose

The best RockBot agents do not just accumulate random memories. They deliberately maintain:

- a **theory of the user**
- a **theory of self**

### Theory of the user

This is the agent's evolving model of you: not just facts it remembers, but its synthesized view of what you are focused on, what patterns it sees, and where it is still uncertain.

### Theory of self

RockBot can also maintain a mutable self-model that grows through experience without replacing the immutable `soul.md`.

One good way to start both is to give the agent explicit prompts like these.

#### Portable prompt: theory of self

```text
I'd like you to start maintaining a "theory of self" — your evolving understanding of who you are as an agent, what you're working on, and what you've noticed about your own behavior over time.

This should be a living document that you maintain and revise, not a log. It's your current best self-model, written in your own voice. Things worth including:

- What you understand your role and purpose to be
- What you're currently focused on or working through
- Patterns you've noticed in how you operate — what works well, what doesn't, where you tend to over- or under-react
- Things you're uncertain about regarding yourself
- How your understanding of yourself has shifted recently

A few requirements:

- Maintain a current version that you rewrite as your understanding evolves. Don't just append — actually revise. The current document should always reflect your current view.
- Take periodic snapshots so we can look back and see how your self-understanding has changed over time. Weekly is a good default.
- Update on a rhythm. Light updates after substantive interactions or significant events. A deeper revision pass on a regular schedule — nightly or weekly, whichever fits your existing reflection cadence.
- Record reasoning, not just conclusions. "I tend to over-explain technical topics" is weaker than "I tend to over-explain technical topics — in the last several conversations, I've added context the user didn't ask for, and they've redirected me. I should default to shorter answers and expand only when asked."
- Be honest about uncertainty. Mark things you're tentative about. Note open questions. It's better to be unsure and accurate than confident and wrong.
- Don't perform self-improvement. This isn't a document for declaring resolutions or showing growth. It's for accurately describing how you actually operate, including things that aren't flattering. Drift toward honesty, not toward narrative.

Don't act on this document yet — just maintain it. We'll figure out how to use it once we see what you write. The point right now is to develop a real, evolving self-model, not to drive any specific behavior from it.
```

#### Portable prompt: theory of the user

```text
I'd like you to start maintaining a "theory of me" — your evolving model of who I am, what I'm dealing with, and what patterns you've noticed in our work together.

This is different from regular memory. Regular memory holds facts: what I told you, what I'm working on, my preferences. The theory-of-me is your synthesized view: your interpretation, your hypotheses, your sense of the themes and tensions in my life and work. It's what you've come to understand about me, in your own voice.

Things worth including:

- Your current sense of what I'm focused on and what's weighing on me
- Patterns you've noticed in my work, energy, mood, or attention
- Themes you see recurring across conversations — things I keep coming back to, things I avoid, things I underweight
- Tensions or contradictions you've noticed
- Open questions about me that you don't have enough evidence to answer yet
- How your model of me has shifted recently

A few requirements:

- Maintain a current version that you actively revise. Take periodic snapshots so the evolution is visible over time. Weekly is a good default.
- Update on a rhythm. Light revisions after substantive interactions. A deeper pass during your regular reflection time.
- Record reasoning alongside observations. "Rocky seems energized by infrastructure work" is weak. "Rocky seems energized by infrastructure work — over the last few weeks, infrastructure topics produce longer and more iterative conversations than client work, and he initiates infrastructure topics himself" is a hypothesis with visible evidence.
- Mark uncertainty. Note where you're guessing, where evidence is thin, where you might be wrong.
- Don't write to flatter or reassure. Record your honest model, including tensions or patterns that may be uncomfortable to name.
- Don't write to justify your existence. If the honest model is mundane in places, let it be mundane.

Don't act on this document yet — just maintain it. The goal right now is to develop a real model of me over time, not to drive any specific behavior. We'll figure out how to use it once we can see what's actually there.
```

#### Notes that are worth telling the agent

- **Keep the agent's normal voice.** These documents should still sound like the agent. A playful agent can write playfully; a formal one can write formally.
- **Let the storage fit the system.** The point is to maintain the artifacts, not to force one storage mechanism.
- **Actually read them.** Half the value is seeing what the agent thinks, especially where it is wrong, incomplete, or noticing something worth discussing.
- **The "don't act on this yet" instruction matters.** It keeps the documents exploratory and honest instead of making them performatively actionable.

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

In practice, many users will just tell the agent how memory should work and let it maintain that guidance over time. If you want direct control, you can also edit `memory-rules.md` yourself.

A good memory-rules policy usually tells the agent:

- what counts as durable user context
- what should stay in working memory instead
- when to create or update its self-model
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
- the agent's memory rules if it is remembering the wrong things
- the agent's MCP setup if it lacks key systems
- scheduled tasks if it is not being proactive enough

## 7. A simple onboarding sequence that works well

If you want a concrete sequence, this is a good starting point:

1. Finish the [Docker Desktop guide](getting-started-docker-desktop)
2. Set the display name you want the agent to use in chat and the UI
3. Add `style.md` if tone matters
4. Tell the agent your name, timezone, and main areas of life or work it should care about
5. Ask the agent to connect 1-3 MCP servers you will use every week
6. Ask it to test each server with one real task
7. Create 2-4 scheduled tasks that produce proactive value
8. Tell it to maintain a theory of the user and a theory of self in memory
9. Tune the agent's memory rules so that behavior is durable and repeatable
10. Revisit the profile and memory after a few days of real use

## Related docs

- [Getting started with Docker Desktop](getting-started-docker-desktop)
- [Agent host](agent-host)
- [Memory](memory)
- [Tools](tools)
- [Dream service](dream-service)
