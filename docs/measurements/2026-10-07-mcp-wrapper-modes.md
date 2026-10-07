# MCP wrapper modes on RockBot's tiers — 2026-10-07

Issue #613, stage 5 of the mcp-aggregator parity work (#618). This measures how the model reaches downstream MCP tools in each mode, on each model RockBot routes to, and chooses the defaults from the result.

The modes:

| Mode | The model gets |
|---|---|
| `Off` | `mcp_invoke_tool` only |
| `Eager` | every typed `{server}__{tool}` tool in every request |
| `Lazy` | `mcp_find_tools`, plus the typed tools the session has activated |
| `Pinned` | lazy, plus every typed tool of a server the session has called |

**Decision: `Pinned` on every tier.** It is the default in `McpToolSurfaceOptions` and in Helm (`agent.mcpWrapperMode`).

## Method

**Harness.** `tools/RockBot.McpMeasure` (see its README) runs RockBot's real agent loop and the real `McpBridgeService` in one process, against fixture MCP servers over loopback HTTP:

| Server | Tools | Notes |
|---|---:|---|
| `adjutant` | 29 | calendar-mcp's tools, with their real parameter shapes |
| `onedrive-marimer` | 4 | same tool names as `onedrive-personal` |
| `onedrive-personal` | 4 | same tool names as `onedrive-marimer` |
| `todo` | 11 | |

That is 48 typed tools in all.

**Tasks.** Five, six turns in all:
- tomorrow's calendar;
- send an email (the `to` argument is an array);
- list a folder on the right one of the two OneDrives;
- add a todo;
- a two-turn thread: find Dana's email, then reply to it.

**Models.** The production tiers, plus one weak model:

| Label | Model | Where |
|---|---|---|
| high | gpt-5.5 | Azure |
| balanced | gpt-5.4 | Azure |
| low | gpt-5.4-mini | Azure |
| weak | qwen3-8b | OpenRouter |

The weak model stands in for a local model. Production runs none, and qwen3-8b is one of the models in mcp-aggregator#42's table.

**Prompt.** The agent's real system prompt (soul, safety rules, common directives, directives, memory rules), about 14k tokens. The directives were the ones on `main` *before* this PR's directive fix, so these numbers describe the prompt production has today.

**Runs.** 10 runs of every task, per model and mode: 960 turns.
- The modes were interleaved within each run.
- OpenRouter rate-limited qwen3-8b in 65 turns. Those task runs were rerun, and no errors remain in the data.

**Metrics.**

| Metric | Meaning |
|---|---|
| **Target right first time** | The first call to the turn's target tool had valid arguments that did what was asked, whatever preparatory calls came first (`list_accounts`, a fresh search). This is the measure for #420's problem. |
| First call correct | mcp-aggregator#42's metric: the first downstream call was the target and was right. |
| Well-formed | The first downstream call reached a real tool with schema-valid arguments. |
| Completed | The target fixture tool received a valid call during the turn. |

Raw data: [`raw/2026-10-07/results.jsonl`](raw/2026-10-07/results.jsonl). To regenerate the tables, run `--summarize` on that folder.

## Results

| Model | Mode | Target right first time | First call correct | Well-formed | Completed | Tool calls | Input tokens / turn | Wall s |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| high (gpt-5.5) | Off | 100% | 83% | 100% | 100% | 5.4 | 85,261 | 22.7 |
| | Eager | 100% | 85% | 100% | 100% | 1.6 | 43,990 | 10.5 |
| | Lazy | 100% | 95% | 100% | 100% | 3.8 | 64,384 | 14.8 |
| | Pinned | 100% | 92% | 100% | 100% | 3.5 | 61,713 | 12.5 |
| balanced (gpt-5.4) | Off | 83% | 58% | 88% | 83% | 3.2 | 51,379 | 12.0 |
| | Eager | 93% | 83% | 100% | 93% | 1.6 | 38,062 | 8.0 |
| | Lazy | 93% | 80% | 97% | 93% | 2.8 | 47,133 | 12.3 |
| | Pinned | **98%** | 83% | 100% | 98% | 2.8 | 48,490 | 12.1 |
| low (gpt-5.4-mini) | Off | 98% | 78% | 98% | 98% | 4.0 | 64,023 | 16.1 |
| | Eager | 100% | 85% | 100% | 100% | 1.8 | 47,082 | 11.2 |
| | Lazy | 92% | 85% | 95% | 100% | 3.7 | 59,906 | 16.6 |
| | Pinned | 95% | 83% | 98% | 100% | 3.5 | 61,698 | 11.6 |
| weak (qwen3-8b) | Off | 55% | 52% | 58% | 100% | 2.9 | 53,757 | 50.7 |
| | Eager | **97%** | 92% | 100% | 100% | 1.1 | 41,559 | 33.2 |
| | Lazy | 63% | 60% | 63% | 98% | 2.8 | 53,286 | 52.8 |
| | Pinned | 65% | 58% | 67% | 100% | 2.5 | 55,282 | 50.3 |

**Caching.** On Azure, 85–97% of input tokens were cached (the system prompt and tool list are a stable prefix), so the cost differences are smaller than the token counts suggest. OpenRouter reported no caching for qwen3-8b.

**Why the first call failed** (counts of turns):

| Model | Mode | invented server | wrong key | wrong type | wrong value | no attempt |
|---|---|---:|---:|---:|---:|---:|
| balanced | Off | | 1 | | | 6 |
| low | Lazy | | 1 | 2 | 2 | |
| qwen3-8b | Off | 12 | 5 | 8 | 4 | |
| qwen3-8b | Lazy | 5 | 10 | 7 | | |
| qwen3-8b | Pinned | 1 | 17 | 2 | 2 | |
| qwen3-8b | Eager | | | | 2 | |

**The table leaves out `other_tool`, which isn't a failure.** That outcome means a sensible preparatory call came first (`list_accounts`, or a fresh mail search before a reply). It accounts for nearly every other miss on the gpt models. On `mcp_invoke_tool`, qwen3-8b invented server names such as `email-mcp`, `ms365` and `email-server`.

## Findings

1. **Typed tools help, most where the model is weakest.**
   - qwen3-8b got the target right first time 97% of the time with eager typed tools, against 55% through `mcp_invoke_tool`. That matches mcp-aggregator#42's result for the same model.
   - On balanced gpt-5.4, `Off` was the worst mode (83%) and the only one where the model sometimes made no call at all (6 turns).
   - gpt-5.5 was right every time in every mode.
2. **Eager is the cheapest and fastest when it fits.**
   - It needs one or two round trips instead of three to five, so it used the fewest input tokens per turn on every model, despite carrying all 48 tool schemas.
   - `Off` was the most expensive: gpt-5.5 used 85k input tokens a turn and made 5.4 calls.
3. **Lazy costs weak models accuracy; pinned recovers some of it.**
   - qwen3-8b managed 63% lazy and 65% pinned, failing mostly on keys and types after the discovery step.
   - The gpt tiers handled it: 92–100%.
   - Pinned matched or beat lazy on every tier: balanced 98% vs 93%, low 95% vs 92%, qwen3-8b 65% vs 63%. It was faster on low (11.6 s vs 16.6 s) because a second call to the same server needs no search.

## Constraint: eager doesn't fit production

OpenAI and Azure reject a request with more than 128 tools. This was tested against the Azure deployment: 128 tools are accepted; 129 get `Invalid 'tools': array too long ... maximum length 128`.

The main agent already sends 56 tools of its own (memory, skills, rules, scheduling, subagents, A2A, web, files, scripts, task list). Production's 8 MCP servers expose 92 tools:

| Server | Tools |
|---|---:|
| calendar-mcp | 29 |
| github | 18 |
| introspection | 12 |
| todo | 11 |
| onedrive-marimer | 7 |
| onedrive-personal | 7 |
| openrouter | 6 |
| azure-foundry | 2 |

Eager would send about 148 tools, so every main-agent request would fail. Eager is possible only for small tool lists, such as a worker with a narrow `tools_allow`, or with fewer servers.

## Live check on 0.16.2 (lazy)

0.16.2 was deployed in `Lazy` mode, and three read-only requests were sent through the CLI. All three were routed to the Low tier.
- **Calendar.** The model never used a typed tool. It called `mcp_list_services`, then `mcp_invoke_tool` six times for `get_calendar_events`, every time with wrong keys: `start`/`end` instead of `startDate`/`endDate`, and no `timeZone`. The gateway's recovery added `timeZone`; the date keys were passed through as sent. This is the `wrong_key` failure typed tools exist to prevent, and lazy mode didn't prevent it, because the model never searched.
- **Mail search.** Workers called `mcp_get_service_details`, which activated `calendar-mcp__search_emails`, and then made correct typed calls.
- **OneDrive.** Failed with the tenant's "no SPO license" error, which is unrelated.

In pinned mode, the calendar case changes: the first `mcp_invoke_tool` call to calendar-mcp pins its 29 typed tools, so the next request offers `calendar-mcp__get_calendar_events` with its schema.

## Decision

**Mode.** `Pinned` on every tier: `McpBridge:WrapperMode` defaults to `Pinned`, and so does Helm's `agent.mcpWrapperMode`. `McpBridge:WrapperModeByTier` can still choose `Eager` for a tier whose tool lists fit, and `Lazy`/`Off` stay available.

**Guards added with it.** These keep a pinned or lazy run under the cap:
- A session's pinned servers share the 40-tool activation budget (`MaxActivatedToolsPerSession`). The most recently called server fills it first.
- Typed tools join a run's list only while it holds at most `McpBridge:MaxToolsPerRequest` tools (default 120). That leaves room for the tools the loop appends after them.
- The main agent tops out at 56 + `mcp_find_tools` + 40 = 97 tools.

**Prompts.** `common-directives.md` told the model to call MCP tools by bare names "already in your tool list", which is never true. It, the worker directives and the patrol seed now describe typed tools and `mcp_invoke_tool` correctly. The live agent seeds its directive files only when they're missing, so the PVC copies need updating by hand.

## Caveats

- **The harness's tool list is smaller than production's.** It offers the MCP tools only (6–54), not the agent's other 50. The fixture's 48 typed tools are about half of production's 92. Both make eager look cheaper here than it would be in production, and neither affects the comparison between lazy and pinned.
- **The second reply turn mostly measures a re-search.** On turn 2 the model sees only turn 1's text reply, as production's conversation memory replays it. So "first call correct" on `reply_thread#2` mostly measures whether the model searches again, which is reasonable; "target right first time" doesn't.
- **10 runs per cell.** A difference of one or two turns in 60 is noise.
