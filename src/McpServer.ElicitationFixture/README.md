# McpServer.ElicitationFixture

An MCP server whose tools ask the caller a question mid-call. It exists so elicitation hand-back
(`design/mcp-elicitation-handback.md`), and the chat's question form (#660), can be exercised
against a live agent instead of only in tests.

## Why it exists

Hand-back lets a server's question go to the agent, or straight to the user as a form in the
Blazor chat. No server RockBot talks to in a normal deployment elicits, so without this server
the path is only verifiable in isolation.

Every tool asks the way hand-back is built for: under MRTR it throws `InputRequiredException`
with its question and a round counter, holds nothing, and reads the answer from the retried call.

## Tools

| Tool | Asks for | What it exercises |
|---|---|---|
| `pick_meaning` | One of five titled choices | A single-select field rendered as a dropdown (more than four options) |
| `confirm_overwrite` | Yes/no | A **decision** field: only the user may answer it, and the form preselects nothing |
| `book_table` | Whole number 1–12, a date, an optional note up to 200 chars | Number range, `date` format and optional fields. The note's title and description contain HTML and markdown, which must show **literally** |
| `choose_toppings` | One to three of five toppings, optional email | Multi-select with min/max, `email` format |
| `two_step` | A name (3–40 chars), then a yes/no confirmation | A second question after the first answer, so a second form |
| `ping` | Nothing | Control: checks the server is reachable |

Each result echoes what was answered, or says it was declined.

## Running it

Locally:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5198 dotnet run --project src/McpServer.ElicitationFixture
curl http://127.0.0.1:5198/health
```

In-cluster, for a live test against the agent:

```bash
VERSION=$(grep -oP '<Version>\K[^<]+' Directory.Build.props)
docker build -f src/McpServer.ElicitationFixture/Dockerfile \
  -t rockylhotka/rockbot-elicitation-fixture-mcp:$VERSION .
docker push rockylhotka/rockbot-elicitation-fixture-mcp:$VERSION

kubectl apply -f deploy/k8s/mcp-elicitation-fixture.yaml
```

Then register it in the agent's `mcp.json` on the PVC, with hand-back mode and a short TTL so
expiry can be watched:

```json
"elicitation-fixture": {
  "type": "http",
  "url": "http://mcp-elicitation-fixture.rockbot.svc.cluster.local/",
  "elicitation": {
    "mode": "handback",
    "handbackTtlMinutes": 2
  }
}
```

Hand-back is opt-in per server, in the operator's own entry; a server registered by the model
can't opt in. For a local docker-compose agent, use `http://host.docker.internal:5198/` as the
URL.

## What to check

In the Blazor chat, ask the agent to use one of the tools (for example, "overwrite the rows with
confirm_overwrite"):

- The question appears as a form, and the agent points to it rather than asking in prose.
- Submitting resumes the call; the agent's next reply reports the tool's result.
- Decline, a refused answer (out of range, too short), expiry after the TTL, and `two_step`'s
  second form all behave as the form says.
- The CLI, which can't show a form, still gets the question in prose and answers through
  `mcp_answer`.

## Resource watches

The server also serves watchable text resources for resource watches (`mcp_watch_resource`,
#768; `design/mcp-bridge.md`, "Resource watches"):

| Piece | What it is |
|---|---|
| `fixture://watch/{name}` | A resource template. Reading a name that hasn't been set fails as not found |
| `set_watch_resource(name, text)` | Sets (or creates) a resource and pushes the change to every watcher |
| `delete_watch_resource(name)` | Deletes it; a watcher's next read fails, which ends its watch |

Its `subscriptions/listen` handler works like the scheduler's: it acknowledges only names that
exist, tags each notification with the listen request's id, sends no keepalives, and ends each
stream after 25 minutes (`Fixture__StreamMinutes` to change it) so the client reconnects.

To try it, add `"subscriptions": true` to the fixture's `mcp.json` entry, then in a chat:

- ask the agent to set `fixture://watch/poll` to some text, then to watch it;
- change it with `set_watch_resource` from another conversation (or `rockbot` CLI session), and
  check that a new turn arrives in the watching conversation with the new text, once per burst;
- restart the agent pod, change the text, and check the change still arrives after it reconnects;
- delete it with `delete_watch_resource` and check the conversation is told the watch ended.

## Cleaning up

```bash
kubectl delete -f deploy/k8s/mcp-elicitation-fixture.yaml
```

and remove the `elicitation-fixture` entry from `mcp.json`. Nothing else is left behind.
