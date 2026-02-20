# Blazor UI (`RockBot.UserProxy.Blazor`)

The Blazor UI is a standalone ASP.NET Core Blazor Server application that provides a real-time
chat interface to the agent. It communicates with the agent exclusively through the RabbitMQ
message bus — it has no direct reference to the agent host and no access to agent internals.

---

## Architecture

```
Browser (SignalR)
    │
    ▼
Blazor Server (RockBot.UserProxy.Blazor)
    │   ChatStateService  ─── in-memory chat state, event-driven UI updates
    │   BlazorUserFrontend ── IUserFrontend impl, routes replies into ChatStateService
    │
    ▼
UserProxyService (RockBot.UserProxy)
    │   Publishes: user.message, user.feedback, conversation.history.request
    │   Subscribes: user.response.{proxyId}, conversation.history.response.{proxyId}
    │
    ▼
RabbitMQ (rockbot topic exchange)
    │
    ▼
Agent (RockBot.Cli)
```

The Blazor UI is stateless with respect to the agent — it holds only the current browser
session's message history in memory (`ChatStateService`). Agent-side persistence (memory,
skills, conversation history) lives on the agent's PVC.

---

## Key components

### `UserProxyService`

Hosted service that owns the RabbitMQ connection on the Blazor side:

- **Subscribe** to `user.response.{proxyId}` on startup — all agent replies arrive here
- **Publish** `user.message` to send user input to the agent
- **Publish** `user.feedback` to send thumbs-up / thumbs-down signals
- **Publish** `conversation.history.request` and await a correlated history response on
  first render

Each outbound message carries a `CorrelationId`. Incoming replies are matched by correlation
ID to a pending `TaskCompletionSource<AgentReply>`. Unmatched replies (unsolicited agent
messages) are routed to `IUserFrontend.DisplayReplyAsync`.

`IsConnected` and `OnConnectionChanged` are exposed so the UI can show a connection indicator.

**Default reply timeout:** configurable via `UserProxyOptions.DefaultReplyTimeout`.

### `ChatStateService`

Singleton in-process state store for the current browser session:

| Method | Purpose |
|---|---|
| `LoadHistory(turns, sessionId)` | Populate from agent's conversation history on first render |
| `AddUserMessage(content, userId, sessionId)` | Echo the user's message immediately (optimistic) |
| `AddAgentReply(reply)` | Add the agent's final reply |
| `SetThinkingMessage(message)` | Update the "thinking" spinner text from intermediate replies |
| `SetProcessing(bool)` | Show/hide the thinking indicator |
| `RecordFeedback(messageId, isPositive)` | Mark a message with thumbs-up or thumbs-down |
| `AddError(message)` | Add an error bubble |

`OnStateChanged` fires after every mutation — the `Chat.razor` component subscribes and calls
`StateHasChanged` to trigger a re-render.

### `BlazorUserFrontend`

`IUserFrontend` implementation that bridges the `UserProxyService` callback into
`ChatStateService`. Handles both normal replies (`DisplayReplyAsync`) and error messages
(`DisplayErrorAsync`).

---

## Chat page (`Chat.razor`)

Single-page application at `/`.

### Message rendering

Agent replies are rendered as Markdown using [Markdig](https://github.com/xoofx/markdig) with
`AdvancedExtensions` (tables, task lists, footnotes, etc.). User messages are rendered as plain
text. Error messages use a danger-styled bubble.

### Input behaviour

| Interaction | Effect |
|---|---|
| `Enter` | Submit message |
| `Shift+Enter` | Insert newline (multiline input) |
| `Up` / `Down` arrow | Cycle through input history (last 50 messages, stored in JS) |
| Window focus | Re-focus the input automatically |

### Thinking indicator

While the agent is processing, a spinner bubble appears. The text updates in real-time from
intermediate `AgentReply` messages (`IsFinal = false`) — these show the agent's current tool
call or reasoning step without a full re-render.

### Scroll behaviour

When a new message arrives the page scrolls to the **top** of the new message bubble, not the
bottom — so long agent responses are read top-to-bottom rather than starting mid-reply.

### Feedback

Every agent reply shows a 👍 / 👎 bar. Clicking either:
1. Marks the message in `ChatStateService` (disabling the buttons to prevent double-voting)
2. Publishes a `UserFeedback` message to RabbitMQ
3. The agent receives it as a `FeedbackSignalType.Correction` (👎) or `ThumbsUp` signal

Feedback flows into the agent's `IFeedbackStore` and influences the dream optimization pass.

### Conversation history on reconnect

On first render (after SignalR circuit establishment — not during static prerendering),
`GetHistoryAsync` requests the full conversation history from the agent via RabbitMQ. This
means a page reload or new browser tab restores the conversation from the agent's in-memory
store rather than starting blank.

### Dark mode

Detects the browser's `prefers-color-scheme` on load and allows manual toggle. Dark mode state
is scoped to the component lifetime (not persisted across refreshes).

### Timezone

Reads the browser's IANA timezone via `Intl.DateTimeFormat().resolvedOptions().timeZone` and
converts message timestamps to the local timezone for display.

---

## Deployment

The Blazor UI runs as a separate Kubernetes deployment (`rockbot-blazor`) with its own
Docker image (`rockylhotka/rockbot-blazor`). It requires only:

- `RABBITMQ__HOST`, `RABBITMQ__PORT`, `RABBITMQ__USERNAME`, `RABBITMQ__PASSWORD` — message bus
  connection (injected via ConfigMap + Secret)

It does **not** need access to the agent data PVC or any agent-internal configuration.

The UI is exposed on the Tailscale network via the Tailscale Kubernetes Operator:

```yaml
blazor:
  tailscale:
    hostname: "rockbot"   # accessible at http://rockbot on your tailnet
```

---

## Configuration

```csharp
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; }          // Unique identifier for this proxy instance
    public TimeSpan DefaultReplyTimeout { get; set; }  // How long to wait for an agent reply
}
```

DI registration in `Program.cs`:

```csharp
builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));
builder.Services.AddUserProxy();
builder.Services.AddSingleton<IUserFrontend, BlazorUserFrontend>();
builder.Services.AddSingleton<ChatStateService>();
```

---

## Message bus topics

| Topic | Direction | Purpose |
|---|---|---|
| `user.message` | Blazor → Agent | User input |
| `user.response.{proxyId}` | Agent → Blazor | Agent replies (final and intermediate) |
| `user.feedback` | Blazor → Agent | Thumbs-up / thumbs-down |
| `conversation.history.request` | Blazor → Agent | Request history on reconnect |
| `conversation.history.response.{proxyId}` | Agent → Blazor | Correlated history response |
