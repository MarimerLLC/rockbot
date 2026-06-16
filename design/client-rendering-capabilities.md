# Client Rendering Capabilities

Status: **implemented** (v1 — sanitizer + CLI strip remain as follow-ups; see "Deferred" below).

## Why

Today every user-facing reply the agent produces is treated as plain markdown.
The Blazor UI renders it through Markdig (`Chat.razor:130, :154, :169, :251`); the
CLI prints it through Spectre or a plain console; future Discord/WhatsApp/Slack/Teams
proxies would each render it however their platform handles markdown-ish text.

That floor is fine, but it leaves real value on the table. A scheduled "daily
metrics" task could include a colored status bar or a small inline SVG chart. A
diff-review reply could use a red/green span. An agent answering "what changed
this week" could render a table. The Blazor pipeline already supports all of
this — Markdig's `UseAdvancedExtensions()` passes inline HTML through, and
`(MarkupString)` skips Blazor's HTML encoding — so it would render today with
zero code changes on that path.

The blocker is that the agent doesn't know **which client is going to render
this particular reply**. Emitting `<span style="color:red">…</span>` is great
in Blazor and ugly noise in a terminal or WhatsApp message. We need a way for
each proxy to tell the agent what it can render, and for the agent to scope
its output accordingly.

## Capability model

Rendering capability is a `[Flags]` enum (`ulong`-backed) called
`ClientCapabilities`. It rides on the bus as a single integer — STJ defaults
to numeric enum serialization (no `JsonStringEnumConverter` configured in
`MessageEnvelopeExtensions.cs:10`), so the wire footprint is ~9 JSON
characters regardless of how many bits are set.

```csharp
[Flags]
public enum ClientCapabilities : ulong
{
    None              = 0,

    // Text + markdown subsets (bits 0–15)
    Text                  = 1UL << 0,    // implicit floor — every client supports this
    MarkdownBasic         = 1UL << 1,    // bold, italic, inline code, blockquotes
    MarkdownHeadings      = 1UL << 2,    // # / ## / ###
    MarkdownTables        = 1UL << 3,    // GFM tables
    MarkdownCode          = 1UL << 4,    // fenced code blocks with language hint
    LinkInline            = 1UL << 5,    // [text](url) renders as a clickable link
    MarkdownStrikethrough = 1UL << 6,    // ~~text~~ — GFM, supported by most chat platforms
    MarkdownTaskList      = 1UL << 7,    // - [ ] / - [x] checkboxes — Markdig advanced + Teams

    // Rich rendering (bits 16–31)
    HtmlInline        = 1UL << 16,   // sanitized HTML inside markdown
    SvgInline         = 1UL << 17,   // inline <svg>
    ImageAttachment   = 1UL << 18,   // out-of-band image binaries (deferred — see below)

    // Platform-native UI primitives, reserved for future proxies (bits 32–47)
    DiscordEmbed      = 1UL << 32,
    SlackBlockKit     = 1UL << 33,
    TeamsAdaptiveCard = 1UL << 34,
}
```

Bit gaps between text formatting (0–15), rich rendering (16–31), and
platform-native UI (32–47) keep growth organized. Unknown bits set by a newer
proxy on an older agent are safely ignored by `HasFlag` / mask checks — the
field is forward-compatible by design.

Capabilities are **abstract**, not platform-specific. The vocabulary names what
the agent is allowed to *assume the receiver can render*, not what wire format
to emit. Discord vs. Slack vs. Teams all consume markdown, but each renders a
different subset; the bits describe the intersection rather than the platform.

### Presets

Per-platform capability sets live alongside the enum, so proxies and design
docs share one vocabulary:

```csharp
public static class ClientCapabilityPresets
{
    public const ClientCapabilities Cli =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode;

    public const ClientCapabilities Blazor =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode | ClientCapabilities.LinkInline |
        ClientCapabilities.HtmlInline | ClientCapabilities.SvgInline;

    // Documented in advance; not used by code until those proxies ship.
    public const ClientCapabilities WhatsApp =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Discord =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Slack =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.ImageAttachment;

    public const ClientCapabilities Teams =
        ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode | ClientCapabilities.LinkInline |
        ClientCapabilities.ImageAttachment;
}
```

## Two sources of capability

The agent has to render replies through multiple entry points, and not all of
them have an inbound user message to read capabilities from. Two distinct
sources cover all cases:

### Source 1 — the live user session (per-message)

A new field on `UserMessage`:

```csharp
public sealed record UserMessage
{
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? TargetAgent { get; init; }
    public ClientCapabilities ClientCapabilities { get; init; } = ClientCapabilities.None;
}
```

The Blazor UI sets it to `ClientCapabilityPresets.Blazor` on every send
(`Chat.razor:488`); the CLI sets it to `ClientCapabilityPresets.Cli`
(`ChatCommand.cs:55, :118`). Older proxies that don't set the field default to
`None`, which falls through to the existing markdown-only behaviour.

Because A2A handlers, the subagent runner, and other agent-internal entry
points produce replies that go back to the **same** user session but don't
have the original `UserMessage` in scope, the capability is cached in a small
singleton:

```csharp
public sealed class SessionClientCapabilityStore
{
    private readonly Dictionary<string, ClientCapabilities> _byId
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Set(string sessionId, ClientCapabilities caps);
    public ClientCapabilities Get(string sessionId);    // returns None if absent
    public void Clear(string sessionId);
}
```

Registered as a singleton in `ServiceCollectionExtensions.cs` next to the
other host-side trackers. Lives alongside `SessionStartTracker` — same
shape, same lifetime semantics (per-session metadata cache).

`ISessionTracker` / `SessionBackgroundTaskTracker` is **not** the right home
despite the similar key. Its entries are per-loop: `BeginSession` cancels and
replaces, `EndSession` removes — too short-lived for capability metadata that
needs to persist across A2A callbacks fired hours later.

### Source 2 — the scheduled task definition (per-task)

A scheduled task's reply audience isn't predictable at fire time. The user
could be on a different client (or none) hours after they scheduled the task.
The author's intent at schedule time is the only meaningful signal. A new
field on `ScheduledTask`:

```csharp
public sealed record ScheduledTask(
    string Name,
    string CronExpression,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastFiredAt = null,
    bool RunOnce = false,
    bool IsSystemTask = false,
    string? Directive = null,
    ClientCapabilities ClientCapabilities = ClientCapabilities.None);
```

The field is propagated on the dispatched `ScheduledTaskMessage` so
`ScheduledTaskHandler` doesn't need to re-fetch from
`IScheduledTaskStore` on every fire. The scheduling tool that creates tasks
grows a corresponding optional parameter; when omitted, behaviour stays
markdown-only.

## Mental model

| Source | Represents | Right for |
|---|---|---|
| `SessionClientCapabilityStore` | The user's currently-active rendering surface | Entry points the user is *actively awaiting* — A2A handlers, subagent runner |
| `ScheduledTask.ClientCapabilities` | The task author's declared intent | Scheduled, time-fired entry points where there is no live user wait |

These two signals are distinct and must not be merged. A scheduled task that
fires hours after the user closed their browser must not replay the
originating session's capability — that would emit HTML into whatever
terminal the user happens to be on now. Conversely, an A2A result returning a
few seconds later should follow the user's currently-active surface, not the
preset of whichever subagent produced the work.

## Read sites

Each entry point passes capabilities into `AgentContextBuilder.BuildAsync(...)`
via a new optional parameter:

```csharp
public async Task<List<ChatMessage>> BuildAsync(
    string sessionId,
    string currentUserContent,
    CancellationToken ct,
    string? workingMemoryNamespace = null,
    string? systemPromptOverride = null,
    ClientCapabilities clientCapabilities = ClientCapabilities.None);
```

| Entry point | File | Source |
|---|---|---|
| `UserMessageHandler.HandleAsync` | `UserMessageHandler.cs:73` | `message.ClientCapabilities` directly (and writes to stash for downstream entry points) |
| `A2ATaskResultHandler` | `A2ATaskResultHandler.cs:338` | `store.Get(rawSessionId)` derived from `pending.PrimarySessionId` |
| `A2ATaskStatusHandler` / `A2ATaskErrorHandler` | `A2ATaskStatusHandler.cs:128`, `A2ATaskErrorHandler.cs:132` | same as above |
| `SubagentRunner` (LLM call site) | `SubagentRunner.cs:203` | `store.Get(primarySessionId)` — covers the subagent's progress and completion bubbles, which bypass the primary's LLM |
| `ScheduledTaskHandler` | `ScheduledTaskHandler.cs:126` | `message.ClientCapabilities` (from the task definition) — **does not consult the stash** |

`ClearContextHandler.HandleAsync` (`ClearContextHandler.cs:19`) calls
`store.Clear(message.SessionId)` alongside the existing
`conversationMemory.ClearAsync(...)` so a fresh start drops the cached
capability and the next inbound `UserMessage` re-establishes it.

## Prompt-builder helper

The capability set is translated into a small system-prompt snippet at the
agent, never on the wire. A helper centralizes the translation so that adding
new bits (and new platforms) touches one file:

```csharp
public static class ClientCapabilityPromptBuilder
{
    public static string? Build(ClientCapabilities caps)
    {
        if ((caps & ClientCapabilityMasks.AnyMeaningful) == 0)
            return null;    // None / bare Text / unknown-bits-only → default markdown behaviour

        var allow = new List<string>(8);
        var deny = new List<string>(8);

        if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            allow.Add("**bold**, *italic*, `inline code`, and blockquotes");
        else
            deny.Add("any markdown formatting — emit plain text only");

        if (caps.HasFlag(ClientCapabilities.MarkdownHeadings))
            allow.Add("`#` / `##` / `###` headings");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("headings — the client renders `#` as a literal character");

        if (caps.HasFlag(ClientCapabilities.MarkdownTables))
            allow.Add("GFM-style tables (`| col | col |`)");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("tables — present tabular data as a bulleted or numbered list");

        if (caps.HasFlag(ClientCapabilities.MarkdownCode))
            allow.Add("fenced code blocks with a language hint");

        if (caps.HasFlag(ClientCapabilities.LinkInline))
            allow.Add("inline links — `[text](https://...)`");
        else if (caps.HasFlag(ClientCapabilities.MarkdownBasic))
            deny.Add("`[text](url)` syntax — paste bare URLs so the client auto-links them");

        if (caps.HasFlag(ClientCapabilities.HtmlInline))
        {
            allow.Add(
                "a safe subset of inline HTML embedded in markdown for color or structure: " +
                "`<span style=\"color:#...\">…</span>`, `<table>`, `<details><summary>…</summary>…</details>`");
            deny.Add(
                "`<script>`, `<iframe>`, `<style>`, event handlers, or external `<img src>` to " +
                "untrusted hosts (the client sanitizer strips these anyway)");
        }

        if (caps.HasFlag(ClientCapabilities.SvgInline))
            allow.Add("inline `<svg>` for simple charts (no `<script>`, keep under ~500 lines)");

        if (caps.HasFlag(ClientCapabilities.ImageAttachment))
            allow.Add("image attachments (PNG/JPEG) when a rendered chart conveys more than prose");

        // ... assemble allow/deny lists into a single system message
    }
}

internal static class ClientCapabilityMasks
{
    public const ClientCapabilities AnyMeaningful =
        ClientCapabilities.MarkdownBasic | ClientCapabilities.MarkdownHeadings |
        ClientCapabilities.MarkdownTables | ClientCapabilities.MarkdownCode |
        ClientCapabilities.LinkInline | ClientCapabilities.HtmlInline |
        ClientCapabilities.SvgInline | ClientCapabilities.ImageAttachment |
        NativeUi;

    public const ClientCapabilities NativeUi =
        ClientCapabilities.DiscordEmbed | ClientCapabilities.SlackBlockKit |
        ClientCapabilities.TeamsAdaptiveCard;
}
```

`AgentContextBuilder` calls the builder immediately after the existing
date/time system message (`AgentContextBuilder.cs:74-82`) and appends the
snippet when it's non-null. When the snippet is null the agent gets no
capability instructions and produces plain markdown — the existing default.

`Enum.HasFlag` on a `ulong`-backed enum boxes both operands. Negligible at
the once-per-turn frequency of context build, but if any hot path ever picks
this up, replace with `(caps & X) == X`.

## Proxy-side responsibility

The agent always emits one canonical form: markdown, optionally with
sanitized inline HTML and inline SVG. Translation to platform-native rendering
happens in the proxy.

- **Blazor**: pipe Markdig output through `HtmlSanitizer` (`Ganss.Xss`
  NuGet) with an allow-list — `span`/`div` + `style` (color/background), `table`,
  inline `svg` and shape elements; no `script`/`iframe`/`on*` handlers/external
  `src`. The system-prompt gate is honour-system; the sanitizer is the real
  safety boundary. Add at all four `(MarkupString)` call sites:
  `Chat.razor:130`, `:154`, `:169`, `:251`.
- **CLI**: tag-strip pass in `PlainConsoleFrontend.DisplayReplyAsync`, and an
  ANSI-color translation in `SpectreConsoleFrontend` for the subset Spectre
  supports. Becomes load-bearing once scheduled tasks legitimately produce
  HTML — without it a user on the CLI sees raw `<span style="color:red">`
  markup when a Blazor-author's scheduled task fires.
- **Future Discord/WhatsApp/Slack/Teams**: each proxy translates from
  canonical markdown to its platform format (Discord's mrkdwn variant,
  WhatsApp's `*bold*` syntax, Slack's mrkdwn, Teams' CommonMark subset).
  Platforms with binary image support (all four) can opt into rasterizing
  inline SVG to PNG and attaching it — but only after the deferred
  `AgentReply.Attachments` work below is done.

## Why per-message, not per-session-negotiated

A per-message capability field on every `UserMessage` costs ~9 JSON characters
and a single dictionary lookup. The alternative — a session-start negotiation
where the proxy advertises capabilities once and the agent caches by
`SessionId` — is heavier (new message type, new handler, replay logic if the
agent restarts) and offers no real benefit when the wire cost is already
trivial. The stash exists for entry points without an inbound message in
scope; it is **not** the primary source of truth.

A second reason: a single proxy may have variable surfaces (an "interactive"
CLI vs. a `--print` one-shot). Per-message is naturally per-surface; per-session
would need re-negotiation when the surface changes.

## Deferred

These are deliberately out of scope for v1:

- **`AgentReply.Attachments`** — ✅ **implemented** (issue #416, first slice). A new
  `IReadOnlyList<AgentAttachment>?` field on `AgentReply` carries out-of-band binaries as
  shared-PVC **path references** (`{ mime, path, fileName? }`), reusing the MCP-attachment
  scheme — bytes never ride the bus. Producing side: an `attach_image` LLM tool
  (`AttachmentReplyTools`) validates a model-named file under the shared attachments dir and
  stages it in a session-keyed `ReplyAttachmentBuffer`; `UserMessageHandler` drains the buffer
  onto the final reply (the **user-message path** only in v1). Rendering side: Blazor
  co-mounts the shared PVC read-only and serves bytes from a minimal `/attachments` endpoint,
  rendering images as native `<img>` **outside** the markdown sanitizer (which strips `<img>`
  and `data:` URLs); the CLI prints a placeholder line per attachment.
  Still deferred: scheduled-task / subagent / A2A producing paths (the buffer generalizes
  trivially), SVG→PNG rasterization, chat-platform proxies (none exist yet), and attachments in
  conversation-history replay (replies are ephemeral; history stores text).
- **Platform-native UI emission** — `DiscordEmbed` / `SlackBlockKit` /
  `TeamsAdaptiveCard` bits are reserved in the enum but no tooling produces
  them. Adding them is opportunistic upgrade work in each proxy (e.g., a
  Discord proxy detecting a heading + body + table and rendering as an
  embed). Agent stays format-agnostic; proxy decides.
- **`SavedResponseTools`** for agent-callable saved-response writes. The
  `ISavedResponseStore` and four message handlers exist
  (`SaveResponseRequestHandler.cs:10` explicitly says "deterministic — no LLM
  invocation"), but no `AIFunction`-backed tool is registered today. The
  only sender of `SaveResponseRequest` is the Blazor UI's save button
  (`Chat.razor:625`). Adding `save_response` as an LLM tool would unlock the
  "scheduled chart task saves rich output, sends short notice on broadcast,
  user reads it later from any capable client" pattern. Defer until the
  "I missed the chart because I was on CLI when the patrol fired" pain
  actually shows up.
- **Per-schedule audience filter / targeted delivery** — only send a
  scheduled reply to proxies whose declared capability set matches the task's.
  Possible but requires the broadcast topic to carry capability metadata and
  each proxy to filter on receive. Probably never needed if the saved-responses
  flow above lands first.

## Open question

When a subagent fans out to multiple subagents that each fan out further,
which session's capability flows down? The current design says: every subagent
inherits its primary's capability (looked up by `primarySessionId`). That's
correct for the deep-tree case because the eventual completion bubble flows
to the same user, but it means a subagent's `system` prompt could carry HTML
permission even when the subagent is doing pure machine-to-machine work
(producing structured output another subagent will parse). Not a correctness
problem — the subagent's output goes through the primary's synthesis pass
before reaching the user — but it's wasted prompt tokens. Worth revisiting if
subagent prompt budget ever becomes tight.
