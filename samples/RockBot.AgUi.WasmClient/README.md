# AG-UI reference client (Blazor WebAssembly)

A small chat client for the [AG-UI proxy](../../docs/agui.md) that runs entirely in the browser.
It exists to show a client author how the pieces of the proxy's contract fit together: a run, the
notify stream and its reconnect rules, history, MCP questions in `custom` mode, attachments and
app auth. It is the last phase of [#794](https://github.com/MarimerLLC/rockbot/issues/794).

**It is a sample, not a product.** The [Blazor Server UI](../../docs/blazor-ui.md) is still
RockBot's chat UI, and this does not replace it. Nothing here is packed, containerized or in the
Helm chart.

## What is here

| Project | What it is |
|---|---|
| `samples/RockBot.AgUi.WasmClient` | The app: one page (`Components/Chat.razor`), a generated form for MCP questions, attachments, and the optional Google sign-in. |
| `samples/RockBot.AgUi.Client` | The transport, with no browser dependency, so it is unit-tested and would work in a MAUI or console app too. `AgUiProxyClient` makes the HTTP calls, `AgUiSse` reads either event stream, `AgUiEventMapper` turns AG-UI events into chat events, and `ChatSession` strings them together. |
| `samples/RockBot.AgUi.DevHost` | The real proxy with an in-process message bus and a scripted agent, to run the sample (and its tests) without RabbitMQ, an LLM or a cluster. |
| `tests/RockBot.AgUi.Client.Tests` | Tests for the transport: recorded event streams through the mapper and the reducer, the reconnect logic against a scripted proxy, and end-to-end tests against the dev host. |

The chat's state is not the sample's own. It uses
[`RockBot.UserProxy.ChatState`](../../docs/blazor-ui.md#chat-state-rockbotuserproxychatstate), the
same events, reducer and store the Blazor Server UI uses, and supplies only a transport:
`AgUiEventMapper` does for AG-UI events what `BlazorUserFrontend` does for the message bus's
callbacks. The page renders `ChatStateService.State` and never changes it.

## Run it locally

Two terminals, from the repository root. No workload is needed (`wasm-tools` is optional and only
makes the published app smaller).

```bash
# 1. The proxy, with a demo agent instead of a real one. Listens on http://localhost:5140.
dotnet run --project samples/RockBot.AgUi.DevHost

# 2. The sample. Listens on http://localhost:5180.
dotnet run --project samples/RockBot.AgUi.WasmClient
```

Open <http://localhost:5180>. The demo agent echoes what you send, with progress, a tool call and
a task list. Some words make it do more:

| Say | And the agent |
|---|---|
| `question` | asks an MCP question; the form appears in the chat. Answer or decline it. |
| `notify` | sends output nobody asked for a moment later: a subagent that starts, works and hands in a result, and a scheduled task's report with its origin. |
| `image` | replies with an image attachment. |
| `slow` | runs until you press **Cancel** (or for a minute). |
| `silent` | never starts the turn, so the proxy stops waiting after `UserProxy:ReplyTimeout` (3 minutes). |

The dev host takes `--port` and `--origin` (the browser origins it allows, comma-separated;
`http://localhost:5180` by default). It is for development only: it serves whoever can reach the
port, and keeps everything in memory.

## Run it against a real proxy

1. Deploy the proxy ([`docs/agui.md`](../../docs/agui.md#deployment)) and note the address
   browsers reach it at.
2. **List the sample's origin on the proxy**: `AgUi:Cors:AllowedOrigins` (Helm:
   `agui.cors.allowedOrigins`). Without it the proxy sends no CORS headers and answers the
   sample's requests with `403`. For the local dev server that is `http://localhost:5180`.
3. Point the sample at the proxy in `wwwroot/appsettings.json`:

   ```json
   { "AgUi": { "BaseUrl": "http://rockbot-agui/", "ThreadId": "agui-session" } }
   ```

4. `dotnet run --project samples/RockBot.AgUi.WasmClient`, or host the published files (below).

A page served over `https` can only call a proxy that is also `https` (browsers block the mix),
and the thread id has to be one the proxy allows (`AgUi:AllowedThreadIds`).

### Hosting the published app

```bash
dotnet publish samples/RockBot.AgUi.WasmClient -c Release -o out
```

`out/wwwroot` is the whole app: static files for any web server (nginx, a storage bucket, GitHub
Pages, a box on the tailnet). There is no server side and no Dockerfile on purpose.

- Edit `out/wwwroot/appsettings.json` for the deployment. The publish also wrote
  `appsettings.json.br` and `.gz`; delete them (or compress the edited file again), or a server
  that prefers the pre-compressed copy will serve the old settings.
- Served from a sub-path, change `<base href="/" />` in `index.html` to that path.
- Served from the proxy's own origin (a reverse proxy in front of both), leave `AgUi:BaseUrl`
  empty: the sample then calls the address the page came from, and no CORS origin is needed.

## Configuration

`wwwroot/appsettings.json`, section `AgUi`. The file is downloaded by the browser, so it holds
nothing secret.

| Key | Default | Use |
|---|---|---|
| `BaseUrl` | `http://localhost:5140/` | The proxy's address. Empty means the page's own origin. |
| `ThreadId` | `agui-session` | The AG-UI thread, which is the RockBot session. |
| `Auth:Enabled` | `false` | `true` for a proxy in [app mode](../../docs/agui.md#app-auth): sign in with Google and send the ID token. |
| `Auth:GoogleClientId` | | The OAuth **Web application** client id. Its *Authorized JavaScript origins* must list the sample's origin, and the proxy's `AgUi:Auth:Audiences` must list the id. Never a secret: there is none. |

## App mode (not tested against Google)

With `Auth:Enabled` the sample shows Google's sign-in button (Google Identity Services, loaded by
`wwwroot/js/googleSignIn.js`), takes the ID token Google hands the page (the `credential`) and
sends it as `Authorization: Bearer` on every request, the notify stream included, since the
stream is read with `fetch`, not `EventSource`. Images and links are loaded by the browser
without a header, so their addresses come from `POST /tickets`.

**This path has not been run against Google.** It needs a real OAuth client and a real account,
which the repository has neither of. What is tested, without a browser: that a token is put on
every request, and that an attachment is shown through a ticket
(`ChatSessionTransportTests.WithAToken_…`). What is not: the sign-in script, the button, and a
real token being accepted by a proxy in app mode. Treat it as a starting point.

Known limits of the sign-in: the token is kept in memory (a reload signs in again), and it is not
refreshed in the background. When it expires after about an hour, the proxy closes the notify
stream and answers `401`; the sample shows the sign-in button again.

## How it uses the contract

- **A run.** `POST /agui` with one user message, `questionMode: "custom"` and the capabilities the
  page can really render. The response is read as it arrives; each AG-UI event becomes chat events
  (`AgUiEventMapper.ForRun`). A run that ends with `rockbot.reply_pending` is a timed-out turn;
  its reply arrives later on the notify stream.
- **The notify stream.** One `fetch`-based stream per page, kept open, with `Last-Event-ID` on
  every reconnect and a backoff of 1, 2, 5, 10, then 30 seconds. On `rockbot.connected` (a new
  stream) and on `rockbot.resync` (missed events that can't be replayed) the sample reloads the
  agent's status, `GET /threads/{id}/messages` and `GET /threads/{id}/questions`. A `400` or
  `403` stops it: trying again would change nothing.
- **Questions.** `custom` mode: a question is a `rockbot.mcp_question` event (in the run or on
  the notify stream), shown as a form generated from its fields, and answered or declined with
  `POST /questions/{id}/answer`. The form closes when `rockbot.mcp_question_status` says so.
- **Attachments.** Upload with `POST /attachments` (the file as the body), then name the returned
  reference on the run. A reply's attachments are shown from their `url`.
- **Cancel.** `POST /threads/{id}/cancel`; the open run then ends as cancelled.
- **Markdown.** A deliberately small renderer (`SafeMarkdown`) that encodes everything before it
  adds a fixed set of tags, so HTML in a reply is shown as text. The sample therefore does not
  declare `HtmlInline`, `SvgInline` or `MarkdownTables`.

### Why not `AGUIChatClient`

The design suggested the SDK's `AGUIChatClient` for runs. The sample reads the stream itself with
`AGUI.Abstractions`' event types (and the SDK's own JSON context) instead, for three reasons:

- The notify stream needs each event's SSE `id` for `Last-Event-ID`, and it is not a run (no
  `RUN_STARTED`, no end), so it needs its own reader anyway. One reader for both streams is less
  code than two.
- The chat state wants the events themselves: activity, tool calls, state, and the
  `CUSTOM rockbot.*` events. `AGUIChatClient` is an `IChatClient`; it turns a run into chat
  response updates for a model-shaped API, which is the wrong shape to map from.
- It replays the conversation with every request and models questions as interrupts. The proxy
  ignores the replay, and this client uses `custom` mode.

`AGUIChatClient` works against the proxy (the proxy's conformance tests drive it), and is the
right choice for a client that wants an `IChatClient`.

## Not covered

The proxy has endpoints for these; the sample does not use them.

- **Saved responses** (`/saved`), **feedback** (`POST /feedback`) and **agent-side clear**
  (`POST /threads/{id}/clear`; the sample does react to another client's clear).
- **MCP sign-in** (`/mcp-signin`): `rockbot.mcp_signin_status` events are ignored.
- **`interrupt` question mode**, the AG-UI way. The proxy's conformance tests cover it.
- **HTML previews.** An HTML attachment is a link that opens in a new tab (the proxy serves it
  sandboxed); it is not shown inline.
- **Token refresh** in app mode, and opening the notify stream with `EventSource` and a ticket.
- **Tables and task-list checkboxes** in Markdown, a multi-line input box, drag-and-drop upload.
- **Several threads.** One page, one thread.
- **What history can't give back** after a reload or a resync: progress and activity logs
  ([`docs/agui.md`](../../docs/agui.md#reconnecting)). Earlier unsolicited replies keep their origin,
  and scheduled-task and inbound A2A output is in history (#809).

## Tests

```bash
dotnet test tests/RockBot.AgUi.Client.Tests
```

No browser, broker or network is involved. The end-to-end tests start the dev host in memory.
