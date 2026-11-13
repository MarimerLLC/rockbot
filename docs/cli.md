---
title: CLI
nav_order: 17
---

# CLI (`RockBot.UserProxy.Cli`)

The `rockbot` CLI is a user proxy on the RabbitMQ bus, like the Blazor UI. With no arguments it runs
an interactive chat. With `--message` it sends one message, follows the turn to its end, and exits.
That one-shot form is how scripts and live tests drive the agent.

## Subcommands

| Command | What it does |
|---|---|
| `chat` (default) | Interactive chat, or one turn with `--message <TEXT>` (`-` reads stdin) |
| `info` | The agent's name and version |
| `status` | The agent's current activity (processing, subagents) |
| `history` | Stored conversation history for a session |
| `clear` | Reset the agent's in-memory conversation context for a session |
| `cancel` | Cancel in-flight work for a session |
| `auth mcp` | Sign in to an OAuth-protected MCP server the agent uses |

Connection options work on every subcommand: `--rabbitmq-host`, `--rabbitmq-port`, `--rabbitmq-user`,
`--rabbitmq-password`, `--agent`, `-s|--session`, `-u|--user`, `--timeout`, `--proxy-id`, `-v|--verbose`.
They override the matching configuration keys (`RabbitMq:HostName` and so on) from `appsettings.json`,
environment variables and user secrets. Put the subcommand first: `rockbot clear --session X`.

## One-shot output

By default (`--output text`), the final reply goes to stdout and progress lines go to stderr:

```
$ rockbot chat -m "look up issue 797 in MarimerLLC/rockbot"
CLI option to write turn events as JSON lines
```

### JSON lines (`--output jsonl`)

`--output jsonl` writes every event of the turn to stdout instead, as one
[AG-UI 1.0](https://docs.ag-ui.com/concepts/events) event object per line, serialized with the AG-UI
.NET SDK (`AGUI.Abstractions`). It needs `--message`. Stdout then carries nothing but these lines,
encoded as UTF-8, and stderr keeps only `error:` diagnostics and logging.

```
{"type":"RUN_STARTED","threadId":"cli-session","runId":"e4e7…","protocolVersion":"1.0","timestamp":1794547308650}
{"type":"CUSTOM","name":"rockbot.agent_run_started","value":{"agentName":"RockBot","heartbeatIntervalSeconds":15},…}
{"type":"TOOL_CALL_START","toolCallId":"call_p6od…","toolCallName":"github__get_issue",…}
{"type":"TOOL_CALL_ARGS","toolCallId":"call_p6od…","delta":"{\"summary\":\"owner=MarimerLLC, repo=rockbot, issueNumber=797\"}",…}
{"type":"TOOL_CALL_END","toolCallId":"call_p6od…",…}
{"type":"TOOL_CALL_RESULT","toolCallId":"call_p6od…","content":"{ \"url\": …","metadata":{"rockbot":{"status":"finished","failed":false,"durationMs":948,"agentName":"RockBot"}},…}
{"type":"TEXT_MESSAGE_START","messageId":"1939…","role":"assistant","name":"RockBot",…}
{"type":"TEXT_MESSAGE_CONTENT","messageId":"1939…","delta":"CLI option to write turn events as JSON lines",…}
{"type":"TEXT_MESSAGE_END","messageId":"1939…",…}
{"type":"RUN_FINISHED","threadId":"cli-session","runId":"e4e7…",…}
```

The first line is always `RUN_STARTED` and the last is always `RUN_FINISHED` or `RUN_ERROR`, even
when the CLI can't reach the broker. Every event has a `timestamp` in milliseconds since the Unix
epoch. The `runId` is the turn's correlation id on the bus.

| Event | When |
|---|---|
| `RUN_STARTED` | First line, before connecting. `threadId` is the session. |
| `CUSTOM rockbot.attachment_sent` | Each `--attach` file the agent accepted (`value.attachment`) |
| `CUSTOM rockbot.agent_run_started` | The agent began the turn (`agentName`, `heartbeatIntervalSeconds`) |
| `CUSTOM rockbot.heartbeat` | The agent is still working (`elapsedSeconds`, `status`) |
| `ACTIVITY_SNAPSHOT` (`activityType: rockbot.progress`) | Progress text (`content.agentName`, `content.text`). Each one replaces the last for its source. |
| `TOOL_CALL_START`, `TOOL_CALL_ARGS`, `TOOL_CALL_END` | A tool call started. RockBot carries a readable argument summary, not the JSON arguments, so `delta` is `{"summary": "…"}`. |
| `TOOL_CALL_RESULT` | A tool call finished or failed. `content` is the result summary or the error, and `metadata.rockbot` holds `status` (`finished`/`failed`), `failed`, `durationMs`, `error` and `agentName`. `rawEvent` is RockBot's `ToolCallInfo`. |
| `STATE_SNAPSHOT`, `STATE_DELTA` | The agent's task lists, as state `{"taskLists": {"primary": [...], "<subagent id>": [...]}}`. Each item has `id`, `description` and `status` (`pending`, `in_progress`, `completed`). Deltas are RFC 6902 patches. |
| `SUBAGENT_STARTED`, `SUBAGENT_FINISHED`, `SUBAGENT_ERROR` | A subagent or worker this session spawned. `name` is `subagent` or `worker`. `SUBAGENT_ERROR.code` is `failed`, `cancelled` or `timeout`. |
| `TEXT_MESSAGE_START`, `TEXT_MESSAGE_CONTENT`, `TEXT_MESSAGE_END` | A reply, as one content chunk. The turn's own reply comes right before the last line. |
| `CUSTOM rockbot.attachments` | Attachments on that reply (`value.messageId`, `value.attachments`) |
| `CUSTOM rockbot.origin` | Follows a reply that isn't this turn's own, such as a subagent result (`value.messageId`, `value.origin`, `value.source`) |
| `RUN_FINISHED` | The turn succeeded, or was cancelled (`outcome.type: "cancelled"`) |
| `RUN_ERROR` | The turn failed. `code` is `error`, `timeout`, `agent_lost`, `attachment_failed` or `client_error`. |

Events from a subagent or worker carry its id as `subagentRunId`. The process exits after the turn's
own reply, so a subagent that outlives the turn shows `SUBAGENT_STARTED` with no finish; its result
arrives later, in other clients.

For example, to check that a turn called `github__get_issue` and the call succeeded:

```bash
rockbot chat -m "…" --output jsonl > events.jsonl
id=$(jq -r 'select(.type=="TOOL_CALL_START" and .toolCallName=="github__get_issue") | .toolCallId' events.jsonl)
jq -e --arg id "$id" 'select(.type=="TOOL_CALL_RESULT" and .toolCallId==$id) | .metadata.rockbot.failed == false' events.jsonl
```

### Exit codes

Both output formats use the same exit codes.

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | The CLI failed before or outside the turn: `--attach` or `--output jsonl` without `--message`, a missing file, an attachment the agent rejected, or no connection to the broker (in jsonl mode) |
| 2 | No reply (timeout), or an attachment the agent never answered |
| 3 | The agent cancelled the turn |
| 4 | The agent reported an error (any reply is still printed) |
| 5 | Lost contact with the agent (no heartbeat) |

An option Spectre.Console can't parse or validate, such as an unknown `--output` format, exits with
-1 (255 on most shells) before anything runs.
