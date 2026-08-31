# Agent Identity & Profile System

## Overview

The agent identity system provides a file-based, composable way to define an agent's personality, goals, and behavioral constraints. It separates **messaging identity** (`AgentIdentity` — name + instance ID for routing) from **LLM identity** (`AgentProfile` — personality, directives, and style for system prompts).

## Design Decisions

### Why Separate Identity from Profile?

`AgentIdentity` is a messaging-layer concern: it identifies an agent on the bus for routing and correlation. `AgentProfile` is an LLM-layer concern: it defines how the agent behaves when generating responses. These serve different purposes and change at different rates:

- Identity is stable per deployment (set once at startup).
- Profile documents can be swapped between deployments without changing routing.
- Not all agents need an LLM profile (e.g., a pure routing agent).

### Why Markdown Files?

Markdown was chosen over JSON/YAML configuration because:

1. **Human-readable**: Non-developers (prompt engineers, content designers) can author and review personality documents without learning a schema.
2. **Composable**: The `##` heading convention naturally segments documents into named sections that can be individually referenced or overridden.
3. **Convention alignment**: Modern agent frameworks (SOUL.md, CrewAI, Character Cards) use markdown for agent definitions. This makes RockBot compatible with existing community patterns.
4. **Version control friendly**: Markdown diffs are easy to review in pull requests.

### Why Three Document Types?

The soul/directives/style split follows the separation of concerns principle:

| Document | Changes when... | Authored by... |
|----------|----------------|----------------|
| **soul.md** | Agent personality is redesigned | Prompt engineer |
| **directives.md** | Deployment requirements change | Operations / developer |
| **style.md** | Voice/tone needs tuning | Content designer |

This means you can swap directives for a new deployment environment without touching the agent's core personality, or add style polish without risking behavioral changes.

### Why a Hosted Service for Loading?

`AgentProfileLoader` implements `IHostedService` to load profile documents during `StartAsync`, consistent with the existing `AgentDiscoveryService` pattern. This ensures:

- Profile is available before any message handlers run.
- Missing required files fail fast at startup (not on first message).
- The loaded `AgentProfile` is registered as a singleton for injection.

## Document Structure

### soul.md (Required)

Defines who the agent IS — stable personality traits.

```markdown
# Agent Name

Optional preamble text.

## Identity

Core identity description.

## Personality

Behavioral traits and communication style.

## Worldview

How the agent perceives and approaches problems.

## Boundaries

What the agent will and won't do.

## Vocabulary

Preferred terminology and language patterns.
```

### directives.md (Required)

Defines HOW the agent operates — deployment-specific instructions.

```markdown
## Goal

What the agent is trying to accomplish.

## Instructions

Step-by-step operational guidelines.

## Response Format

Expected output structure.

## Constraints

Hard limits on behavior.
```

### style.md (Optional)

Voice and tone polish for user-facing agents.

```markdown
## Tone

Overall communication tone.

## Examples

Sample interactions demonstrating desired style.

## Patterns

Recurring phrases or formatting patterns.
```

## Parsing Rules

- `#` headings flow into the preamble (not section boundaries).
- `##` headings delimit sections.
- Content before the first `##` is the preamble.
- No `##` headings → entire content becomes preamble (permissive).
- Empty documents produce no preamble and no sections.

## System Prompt Composition

`DefaultSystemPromptBuilder` composes the prompt as:

```
You are {agent-name}.

{soul.md raw content}

{directives.md raw content}

{style.md raw content, if present}
```

Custom builders can implement `ISystemPromptBuilder` for more sophisticated composition (e.g., selecting specific sections, adding runtime context).

## Registration

```csharp
builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("my-agent");
    agent.WithProfile();                          // convention: loads from ./agent/
    // or:
    agent.WithProfile(opts =>                     // custom paths
    {
        opts.BasePath = "./my-config/";
        opts.SoulPath = "custom-soul.md";
    });
});
```

## Narrative Identity (Dynamic Self-Model)

While `soul.md` and `directives.md` are static and deployment-controlled, the agent also maintains a **mutable narrative identity** — a set of long-term memory entries under `agent-identity/` that evolve as the agent accumulates experience.

### Categories

| Category | Purpose |
|----------|---------|
| `agent-identity/mission` | How the agent interprets its purpose given experience |
| `agent-identity/goals` | Long-term goals derived from user patterns and feedback |
| `agent-identity/projects` | Active projects and their status |
| `agent-identity/capabilities` | Self-assessed strengths and limitations |
| `agent-identity/self-model` | Overall narrative description of who the agent has become |

### How It Works

1. **Dream service** runs an identity reflection pass during each dream cycle. It reviews recent episodic memories, feedback signals, and user preferences, then updates identity entries when a meaningful shift has occurred.
2. **AgentContextBuilder** injects identity entries into every LLM context. Primary agents see first-person framing ("Your evolving identity..."); subagents and patrol tasks see third-person framing ("Primary agent identity context...") that reinforces their subordinate role.
3. **Users can review and edit** identity entries via the standard `search_memory` / `save_memory` tools using the `agent-identity` category.

### Design Constraints

- **soul.md is immutable** — identity entries complement the soul but can never override core values, boundaries, or personality.
- **Conservative evolution** — the dream directive instructs the LLM to update only when there is a meaningful shift, not every cycle. Target is 1-2 entries per subcategory.
- **Role-aware injection** — subagents see the primary agent's identity as context about the agent they serve, preventing them from assuming the primary's role.
- **Constants** — `AgentIdentityCategories` in `RockBot.Host.Abstractions` defines the well-known category names.

### Example

After weeks of primarily managing email and calendar:

```
Category: agent-identity/self-model
Content: "I have become primarily a communication and scheduling manager
          with research capabilities. Most user interactions involve email
          triage, calendar management, and meeting preparation."
Importance: 0.8
```

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Missing `soul.md` | `FileNotFoundException` at startup (fatal) |
| Missing `directives.md` | `FileNotFoundException` at startup (fatal) |
| Missing `style.md` | `AgentProfile.Style` is null (not an error) |
| Empty document | Valid — no preamble, no sections |
| No `##` headings | Valid — entire content becomes preamble |
| `StylePath` set to null | Style loading skipped entirely |
