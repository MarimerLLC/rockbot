# Voice Interaction

Status: **proposed** — no code written yet.

## Why

Today the Blazor UI is keyboard-only: a textarea in, markdown bubbles out
(`Chat.razor:313-335`). The interaction the user wants is conversational —
talk to RockBot from a phone or a desktop browser and hear it answer, with the
answering voice being something they pick, not something the device picks for
them.

Two separate capabilities are needed, and they have different cost and risk
profiles:

- **Speech in (STT)** — turn the user's speech into the text that already flows
  through `UserProxyService.SendAsync`. Purely additive to the existing send path.
- **Speech out (TTS)** — read agent replies aloud, in a user-selected voice.
  Needs a rendering step (markdown is not speakable), a preference store the UI
  currently does not have, and a state machine so the microphone and the speaker
  don't fight each other.

Both must work in a plain browser on iOS/Android and on the desktop. No native
app, no extension.

## Goals

- Push-to-talk speech input in the Blazor chat, on phone and desktop.
- Agent replies spoken aloud, gated by a user toggle.
- The output voice is selectable in the UI and persists across page loads.
- Zero new secrets and zero new bus traffic for the first shippable version.
- The speech provider is behind a seam, so higher-quality server-side voices can
  replace the browser's without touching the UI or the interaction loop.
- Degrades cleanly: a browser with no speech support gets today's UI, unchanged.

## Non-goals

- Audio on the message bus. RockBot deliberately keeps binaries off RabbitMQ —
  `AgentReply.Attachments` carries shared-volume *path references* precisely so
  bytes never ride the bus (`AgentAttachment.cs`, and the `/attachments`
  endpoint in `Program.cs:44-56`). Voice audio follows the same rule.
- Voice in the CLI proxy. `RockBot.UserProxy.Cli` stays text.
- Wake-word / always-on listening. The mic opens on an explicit gesture.
- Speaker identification, diarization, or voice auth.
- Real-time duplex streaming (interrupting mid-sentence and having the agent
  re-plan). The loop is half-duplex; see "Turn state machine".

---

## Constraints discovered in the current codebase

These shaped the design; each one rules an option in or out.

| Constraint | Where | Consequence |
|---|---|---|
| Blazor **Server** render mode over a SignalR circuit | `App.razor:21`, `Program.cs:58-59` | Every JS↔.NET hop is a network round-trip. Interim transcripts must not cross it. |
| SignalR `MaximumReceiveMessageSize` defaults to 32 KB | ASP.NET Core default; not overridden in `Program.cs` | Audio blobs **cannot** go through `IJSRuntime` interop. Server-side STT would need a minimal-API POST endpoint, not JS interop. |
| Blazor pod env carries **only** RabbitMQ + WorkIQ keys | `deploy/helm/rockbot/templates/blazor/deployment.yaml:36-92` | Server-side speech means a *new* secret and a helm change. The browser needs neither. |
| HTTPS is already in place via Tailscale ingress | `blazor/ingress.yaml:19-24` | `getUserMedia` / Web Speech API's secure-context requirement is satisfied on the tailnet, and `localhost` is a secure context for local dev. No new TLS work. |
| The UI has **no** preference persistence at all — dark mode resets on every load | `Chat.razor:632-645` | Voice choice needs a store built from scratch. `localStorage` is the right one (see "Why per-device"). |
| The input is disabled while `IsProcessing` | `Chat.razor:322` | The mic button must honour the same gate, or the user talks into a locked UI. |
| Replies are markdown rendered through Markdig | `SafeMarkdownRenderer` | Speaking the raw string reads fences, pipes and asterisks aloud. A normalizer is mandatory, not polish. |
| Unsolicited replies bypass `SendMessage` entirely | `BlazorUserFrontend.DisplayReplyAsync` | Scheduled-task and subagent results arrive on a background thread. Speaking them needs an event on `ChatStateService`, not a hook in the send path. |
| One JS file, plain `window.chatHelpers` global | `wwwroot/js/chat.js`, `App.razor:22` | New JS follows the same convention — a sibling `voice.js` exposing `window.rockbotVoice`. |

---

## Where the speech happens

Three places it could live. The decision is the load-bearing one in this design.

**A. In the browser — Web Speech API.** `SpeechRecognition` for input,
`speechSynthesis` for output.

- No API key, no cost per turn, no new secret on the Blazor pod, no audio
  crossing the network at all.
- Voices come from the operating system, so the catalogue differs per device —
  an iPhone offers Apple's voices, a Windows desktop offers Microsoft's. "Choose
  RockBot's voice" therefore means "choose from what *this* device offers".
- Firefox does not ship `SpeechRecognition`; input is unavailable there. Output
  works everywhere.
- Quality is adequate on Apple and Android, noticeably dated on Windows.

**B. On the Blazor host — a cloud speech provider.** Browser records audio,
POSTs it to `/voice/transcribe`; reply text POSTs to `/voice/speak` and streams
back an MP3.

- One consistent voice on every device, and a much better one — this is what
  makes a chosen voice feel like RockBot's *identity* rather than the phone's.
- Works in Firefox.
- Costs a new secret on the Blazor pod, per-turn money, and added latency.
- Must use minimal-API endpoints, not JS interop, because of the 32 KB circuit limit.

**C. On the agent, over the bus.** Publish `voice.tts.request`, get audio back.

- Rejected. It puts binaries on RabbitMQ, which this codebase has explicitly
  designed against, and makes the agent a serialization point for something that
  has nothing to do with reasoning.

**Recommendation: ship A, structure for B.** Not because A is the better end
state — for a chosen voice with character, B probably is — but because every
genuinely hard part of this feature is identical under both: the autoplay gate,
the mic/speaker feedback loop, chunking long replies, mobile lifecycle. A gets
those solved for zero marginal cost and zero new infrastructure. B then becomes
a swap of two functions behind `IVoiceProvider` plus a helm secret, on a loop
that is already proven to work.

---

## Architecture

```
Browser
  ├── voice.js  (window.rockbotVoice)
  │     SpeechRecognition ──► interim text written straight into the textarea DOM
  │     speechSynthesis   ◄── chunked utterance queue
  │     [Phase 4] MediaRecorder ──► POST /voice/transcribe
  │                        ◄── GET  /voice/speak  (audio stream)
  │            │ only final transcript + control events cross the circuit
  ▼            ▼
Blazor Server (SignalR)
  ├── VoiceSettingsPanel.razor ── voice picker, rate/pitch, toggles
  ├── VoiceSettingsService     ── load/save via localStorage
  ├── ChatStateService         ── + OnSpeakableReply event
  └── SpeechTextRenderer       ── markdown ─► speakable plain text
        │
        ▼  (unchanged)
  UserProxyService ──► RabbitMQ ──► Agent
```

Nothing on the agent side changes for Phases 0–2. The agent keeps producing the
same markdown; the client decides how to voice it.

### Turn state machine

Half-duplex, because a live microphone plus a live speaker means RockBot
transcribes itself.

```
        ┌──────────────────────────────────────────────┐
        │                                              │
   ┌────▼───┐  mic tap   ┌───────────┐  final txt  ┌───┴─────┐
   │  Idle  ├───────────►│ Listening ├────────────►│ Sending │
   └────▲───┘            └─────┬─────┘             └───┬─────┘
        │                      │ cancel                │ reply
        │   utterance queue    │                       ▼
        │   drained, or  ┌─────▼──────┐          ┌──────────┐
        └────────────────┤  Speaking  │◄─────────┤ Rendering│
          barge-in tap   └────────────┘          └──────────┘
```

- Entering `Speaking` calls `recognition.stop()` first, unconditionally.
- A tap during `Speaking` is **barge-in**: cancel the utterance queue and go
  straight to `Listening`. This is why the reply is chunked into short
  utterances — `speechSynthesis.cancel()` is only responsive between them.
- Hands-free mode (Phase 3) re-enters `Listening` automatically when the queue
  drains. Everything else returns to `Idle`.

---

## Components

### `SpeechTextRenderer` (new, `RockBot.UserProxy/Rendering/`)

A pure static function, sitting beside the existing `HtmlPlainTextRenderer` —
same job, different target. Markdown in, speakable text out:

- fenced code blocks → `"code block, 12 lines — on screen"` (never read aloud)
- tables → `"a table with 6 rows — on screen"`
- inline `` `code` `` → contents, unadorned
- `**bold**` / `*em*` / `~~strike~~` → contents
- `[text](url)` → `text`; bare URLs → `"a link"`
- headings → sentence + a clause break
- list markers → dropped; each item gets a clause break
- emoji and the SVG placeholder from `HtmlPlainTextRenderer` → dropped
- the origin-anchor blockquote `BlazorUserFrontend` prepends → kept, it's the
  part that tells the user *why* their phone just started talking

Lives in `RockBot.UserProxy` rather than the Blazor project so it is unit
testable without a renderer, and so a future Discord/WhatsApp voice note can
reuse it. Tests go in `RockBot.UserProxy.Tests`.

The chunker is separate and lives in JS: split the normalized text on sentence
boundaries into utterances of roughly 200 characters. This works around
Chromium's long-standing habit of stalling `speechSynthesis` on utterances past
about fifteen seconds, and it is what makes barge-in feel immediate.

### `voice.js` (new, `wwwroot/js/`)

Follows the `window.chatHelpers` convention — a plain script, a single global,
loaded from `App.razor` beside `chat.js`.

```
window.rockbotVoice = {
  support(),                       // { stt: bool, tts: bool }
  listVoices(),                    // [{ voiceURI, name, lang, localService }]
  prime(),                         // speak a silent utterance — unlocks iOS
  speak(chunks, voiceURI, rate, pitch, dotNetRef),
  stopSpeaking(),
  startListening(dotNetRef, lang, textareaEl),
  stopListening()
}
```

Two details matter:

- `listVoices()` must handle `getVoices()` returning empty on first call and
  resolve on the `voiceschanged` event. Chromium populates the list
  asynchronously; a naive call returns `[]` and the picker looks broken.
- `startListening` writes **interim** results directly into the textarea element
  and dispatches `new Event('input', { bubbles: true })` to sync Blazor's
  `@bind` — exactly the technique `initInputHistory` already uses
  (`chat.js:72`). Only the final transcript is pushed to .NET. On a Blazor
  Server circuit this is the difference between live dictation and a stutter.

### `VoiceSettings` + `VoiceSettingsService` (new, `Services/`)

```csharp
public sealed record VoiceSettings
{
    public bool   SpeakReplies      { get; init; }          // master output toggle
    public bool   SpeakUnsolicited  { get; init; }          // scheduled/subagent results
    public string? VoiceUri         { get; init; }
    public string? VoiceName        { get; init; }          // fallback match key
    public string  VoiceLang        { get; init; } = "en-US";
    public double  Rate             { get; init; } = 1.0;
    public double  Pitch            { get; init; } = 1.0;
    public bool    HandsFree        { get; init; }
    public string  RecognitionLang  { get; init; } = "en-US";
}
```

Persisted to `localStorage` under `rockbot.voice.v1`.

**Why per-device rather than agent-side memory:** under provider A the voice
catalogue *is* device-specific — a `voiceURI` from a Mac does not exist on an
Android phone. Storing the choice server-side would mean syncing a preference
that cannot be honoured. Resolution is therefore a graceful ladder:
`voiceURI` → exact `name` → same language prefix → the device default. When
Phase 4 lands, server voices are device-independent and the setting can move to
the agent's user memory as a genuine identity preference; the ladder keeps
working in the meantime.

`SpeakUnsolicited` defaults **off**. A phone in a pocket announcing a scheduled
task result at 3am is a bug report, not a feature.

### `VoiceSettingsPanel.razor` (new, `Components/`)

A modal in the same shape as the existing Saved-responses and WorkIQ modals
(`Chat.razor:247-261`) — overlay div, stop-propagation inner panel, header with
a close button.

Contents: speak-replies toggle; a voice `<select>` grouped by language, showing
`name (lang)` and flagging `localService`; rate and pitch sliders; a **Test
voice** button that speaks a fixed sample line so the choice can be heard before
committing; hands-free toggle; recognition-language select; and a plain-language
line about what this browser does and does not support, so a Firefox user
understands why there is no microphone.

### `Chat.razor` changes

- Header: a 🔊/🔇 toggle bound to `SpeakReplies`, and a ⚙ opening the panel —
  beside the existing M365 / 📋 / 🌙 buttons.
- Input row: a mic button left of Send, disabled under the same
  `ChatState.IsProcessing` condition as the textarea, showing a recording state
  while listening.
- After a final reply lands, if `SpeakReplies` is on, normalize → chunk → speak.
- A stop-speaking control while the queue is draining.

### `ChatStateService` change

Add `event Action<ChatMessage>? OnSpeakableReply`, raised from `AddAgentReply`
for `PrimaryFinal` messages only. This is what lets unsolicited replies be
spoken: they arrive via `BlazorUserFrontend.DisplayReplyAsync` on a bus thread
and never touch `SendMessage`. The handler marshals through `InvokeAsync`, as
the existing `OnStateChanged` subscription does (`Chat.razor:381`).

---

## The parts that will actually be hard

Worth naming, because they are where the time goes — and they are the reason to
prove the loop with the free provider first.

1. **iOS autoplay.** `speechSynthesis.speak()` from a SignalR callback with no
   user gesture behind it is blocked on iOS Safari. Mitigation: call
   `rockbotVoice.prime()` — a silent utterance — on the first user interaction
   (the toggle or the mic tap). That unlocks the origin for the session. There
   is no way around needing *some* gesture, so the first-run UX has to include
   one.
2. **Recognition stops on its own.** Chromium ends a recognition session after a
   few seconds of silence, and `continuous = true` is unreliable on mobile.
   Push-to-talk is therefore the default interaction, not a fallback: tap, speak,
   and auto-submit on `onend` when the transcript is non-empty.
3. **Feedback loop.** Handled by the state machine above; the failure mode if it
   is missed is RockBot transcribing its own voice into a new prompt, which is
   both funny and a runaway cost.
4. **Voice list races.** Covered by the `voiceschanged` handling above; symptom
   is an empty dropdown on a cold load.
5. **Circuit reconnects.** A dropped SignalR circuit leaves JS-side recognition
   running with a dead `DotNetObjectReference`. `voice.js` must hold a
   generation counter and drop callbacks from a stale reference.
6. **Mobile backgrounding.** Locking the phone suspends recognition without
   firing `onend` on some builds. Watch `visibilitychange` and force the state
   machine back to `Idle`.

---

## Phasing

Each phase is independently shippable and independently useful.

| Phase | Scope | Delivers |
|---|---|---|
| **0** | `SpeechTextRenderer` + unit tests | Pure, no UI, no risk. Unblocks everything else. |
| **1** | TTS out: `voice.js` speak path, settings panel, voice picker, `localStorage`, speak on final reply | Half the ask — RockBot talks, in a chosen voice. Nothing can break existing behaviour. |
| **2** | STT in: mic button, push-to-talk, interim-to-textarea, auto-send | The full loop, on desktop and mobile. |
| **3** | Hands-free mode, barge-in, `SpeakUnsolicited`, `ClientCapabilities.SpeechOutput` | Conversational rather than transactional. |
| **4** | Optional: server-side neural voices behind `IVoiceProvider` | Consistent, high-quality voice across devices. New secret + helm change. |

### Phase 3's capability bit

`ClientCapabilities` already reserves bits 16–31 for rich rendering, with 18
(`ImageAttachment`) the highest in use. Bit 19 is free:

```csharp
SpeechOutput = 1UL << 19,   // reply will also be read aloud
```

Set on the outbound `UserMessage` only while `SpeakReplies` is on, so the agent
can keep answers conversational — short sentences, no ASCII tables, no
twelve-item bullet lists — when it knows it is being listened to rather than
read. The enum is documented as forward-compatible (`ClientCapabilities.cs:4-8`),
so setting the bit is safe against an agent that has never heard of it: unknown
bits are ignored by `HasFlag`. The agent-side prompt work is a genuinely separate
change and should not gate Phases 0–2.

Note this is a *hint*, not a mode switch. The Blazor user still sees the bubble
while hearing it, so the reply has to work in both channels at once. That is why
Phases 0–2 normalize client-side rather than asking the agent for a second,
speech-shaped rendering — one reply, two presentations.

---

## Testing

- `SpeechTextRendererTests` (`RockBot.UserProxy.Tests`) — table-driven over
  fences, tables, links, emoji, nested emphasis, and the origin-anchor
  blockquote. Pure function, no mocks needed.
- `VoiceSettingsServiceTests` (`RockBot.UserProxy.Blazor.Tests`) — round-trip
  and the voice-resolution ladder, with `IJSRuntime` mocked via Rocks, matching
  the existing `WorkIqAuthUiServiceTests` pattern.
- `ChatStateServiceTests` — `OnSpeakableReply` fires for `PrimaryFinal` and stays
  silent for progress, activity-log, and error categories.
- The browser-side behaviour (autoplay gates, recognition lifecycle, mobile
  backgrounding) is not meaningfully unit testable. It needs a manual matrix —
  iOS Safari, Android Chrome, desktop Chrome, desktop Firefox, desktop Safari —
  run once per phase. That matrix is the real test suite for this feature and
  should be written down before Phase 1 starts.

## Open questions

1. **Is the browser's voice good enough to be RockBot's voice?** The whole
   phasing rests on the bet that it is good enough to prove the loop. If the
   Windows voices are unacceptable in practice, Phase 4 moves ahead of Phase 3.
   Worth answering empirically in an afternoon before committing to the order.
2. **Auto-send, or confirm before send?** Auto-submitting on `onend` is what
   makes the loop feel conversational, but a misheard transcript then reaches
   the agent unreviewed. A short cancel window — send after ~1.5s unless tapped —
   may be the compromise. Needs to be tried, not argued.
3. **Should the voice choice eventually live in agent memory?** Under Phase 4 it
   becomes a device-independent identity preference and arguably belongs with the
   agent's `style.md` rather than in a browser. Deferred until Phase 4 is real.
4. **Recognition language vs. agent language.** Currently one setting each, both
   defaulting to `en-US` and unrelated to anything the agent knows. Fine for now;
   revisit if multilingual use ever matters.
