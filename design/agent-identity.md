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

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Missing `soul.md` | `FileNotFoundException` at startup (fatal) |
| Missing `directives.md` | `FileNotFoundException` at startup (fatal) |
| Missing `style.md` | `AgentProfile.Style` is null (not an error) |
| Empty document | Valid — no preamble, no sections |
| No `##` headings | Valid — entire content becomes preamble |
| `StylePath` set to null | Style loading skipped entirely |
