# Slack MCP server (docker compose)

Runs `slack-mcp` (`src/McpServer.Slack`), which gives RockBot **read and search** access to one or
more Slack workspaces, as you. RockBot gets six tools, all read-only:

| Tool | What it does |
|---|---|
| `list_workspaces` | The workspaces configured and who RockBot reads as in each |
| `list_conversations` | Channels, private channels, DMs and group DMs you're in |
| `get_history` | Recent messages in a conversation, by date range |
| `get_thread` | A thread's parent and replies |
| `search_messages` | Slack search syntax: `budget in:#finance from:@alice after:2026-10-01` |
| `get_user` | A person's name, title, time zone and email |

The server has no posting, reacting, editing or deleting tools, and the Slack app's scopes
(`deploy/slack/manifest.json`) are read-only too, so the token can't post either.

## One app per workspace

Each workspace gets its own "RockBot" app, created from the same manifest. That keeps each app
*internal* to its workspace:
- Slack keeps the normal rate limits for internal apps. Since 2025 it throttles the history of
  non-Marketplace apps distributed across workspaces to about one request a minute.
- No OAuth redirect has to be hosted: the token is shown on the app's page after you install it.

The token is a user token (`xoxp-`) with rotation off, so it doesn't expire. It reads what you
can read, including DMs, unless you create the app with `--no-dms`.

## Set up

Run this once per workspace, in a terminal:

```bash
deploy/slack/setup.sh --workspace work
```

1. It prints a link that creates the app from the manifest. Pick the right workspace.
2. Under **Install App**, click **Install to Workspace**. If the workspace needs approval, click
   **Request to Install**, wait for the admin, then come back and install.
3. Copy the **User OAuth Token** from **OAuth & Permissions** and paste it into the script.

It checks the token with `auth.test` and warns about missing scopes. It stores
`SLACK_<NAME>_TOKEN`, `SLACK_<NAME>_TEAM_ID` and `SLACK_WORKSPACES` in
`deploy/docker-compose/.env`. Then it writes `slack.env`, starts `slack-mcp` and registers `slack`
with the agent.

| Option | Effect |
|---|---|
| `--workspace NAME` | What RockBot calls the workspace (lower-case letters, digits, `-`) |
| `--no-dms` | Create the app without DM and group-DM scopes |
| `--new-token` | Ask for a new token even if one is stored |
| `--render-only` | Regenerate `slack.env` from `.env` and restart `slack-mcp` |

`slack.env` is generated and git-ignored. It only *references* the tokens in `.env` (as
`${SLACK_WORK_TOKEN}`). If you manage `.env` with another tool, copy the `SLACK_*` keys and the
`slack` profile into it. After that tool rewrites `.env`, run `setup.sh --render-only`.

To remove one workspace, run `deploy/slack/teardown.sh --workspace NAME`; to remove all of them,
run `teardown.sh --all`. It revokes the token. Delete the app itself at
<https://api.slack.com/apps>.

## Security

Messages are written by other people, so they are untrusted input: someone could post text meant
to steer the agent. The tool descriptions tell the model so. And because nothing in this server
can post, a steered agent still can't speak in Slack.
