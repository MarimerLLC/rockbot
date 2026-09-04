# Voice Interaction

Status: **proposed** — no code written yet.

Targets **full-duplex** conversation: the microphone stays open while RockBot is
speaking, and the user can interrupt by talking rather than by tapping. An
earlier draft of this design specified a half-duplex, push-to-talk loop built on
the browser's Web Speech API. That approach cannot deliver full duplex, for
reasons set out under "Why the Web Speech API is out"; it survives here only as
the degraded fallback for browsers that cannot reach a speech provider.

## Why

Today the Blazor UI is keyboard-only: a textarea in, markdown bubbles out
(`Chat.razor:313-335`). The goal is to talk to RockBot from a phone or a desktop
browser and hear it answer, in a voice the user chooses, with the turn-taking
feeling like a conversation rather than a walkie-talkie.

## What full duplex actually requires

The request bundles two problems with very different costs. Separating them is
the most important thing this document does.

**1. Duplex audio** — mic open during playback, interruption by voice, no
self-transcription. This is a client and transport problem: echo cancellation,
voice activity detection, streaming speech-to-text and text-to-speech. Entirely
solvable, and it is the smaller half.

**2. Conversational latency** — the gap between the user finishing a sentence
and RockBot starting to answer. For a conversation to feel like one, that gap
needs to sit somewhere around 500–800 ms. This is where RockBot's architecture
pushes back hard, and no amount of audio-pipeline work fixes it.

Here is the budget, with what the current codebase actually does:

| Stage | Budget |
|---|---|
| Client VAD detects end of speech | 200–500 ms |
| Streaming STT emits final transcript | 100–300 ms |
| Bus round-trip to agent | 10–50 ms |
| **`AgentLoopRunner.RunAsync`** | **2–60 s** |
| TTS time-to-first-audio | 75–400 ms |

`MaxToolIterations` defaults to **50** (`AgentHostOptions.cs:28`) and
`UserProxyOptions.DefaultReplyTimeout` defaults to **three minutes**
(`UserProxyOptions.cs:30`). Those are not accidents — they describe a system
deliberately built for deliberate, multi-step, tool-using turns. The agent is
the bottleneck by one to two orders of magnitude, and everything else on the
list is rounding error beside it.

So: full-duplex audio buys an always-open mic and instant interruption, but
without agent-side work the reply still lands as one block after a long silence.
That is a better walkie-talkie, not a conversation. The agent-side changes in
"Closing the latency gap" are not optional extras — they are half the feature.

---

## Constraints discovered in the current codebase

| Constraint | Where | Consequence |
|---|---|---|
| `ILlmClient` is **non-streaming** — `Task<ChatResponse>` only | `RockBot.Host.Abstractions/ILlmClient.cs` | No token-level output exists anywhere on the RockBot path. The streaming overrides in `ToolStrippingChatClient` / `FallbackChatClient` are `DelegatingChatClient` pass-throughs that RockBot never calls. |
| `AgentLoopRunner.RunAsync` returns `Task<string>` after up to 50 tool iterations | `AgentLoopRunner.cs:306`, `:941` | One complete reply at the end of a loop. This is the mandatory single LLM entry point per `CLAUDE.md`, so it cannot be bypassed. |
| A cancel path already exists | `CancelSessionHandler`, topic `user.cancel.{agent}` | Barge-in has somewhere to land. This is the one piece of the interrupt story already built. |
| `ToolProgressNotifier` publishes per-tool-call progress to the bus | `ToolProgressNotifier.cs` | Sub-turn signals already flow to the client. These become spoken filler for free. |
| No WebSocket infrastructure anywhere in `src` | — | The audio transport is new. `UseWebSockets()` is not currently called. |
| SignalR `MaximumReceiveMessageSize` defaults to 32 KB | ASP.NET default, not overridden in `Program.cs` | Audio **cannot** go over the Blazor circuit, and should not anyway — audio frames would contend with UI diffs. A separate WS endpoint is required. |
| Blazor pod env carries **only** RabbitMQ + WorkIQ keys | `blazor/deployment.yaml:36-92` | A speech provider key is a new secret and a helm change. Unavoidable under full duplex. |
| HTTPS already in place via Tailscale ingress | `blazor/ingress.yaml:19-24` | Secure-context requirement for `getUserMedia` is satisfied; `wss://` works on the same host. No new TLS work. |
| The UI has **no** preference persistence — dark mode resets every load | `Chat.razor:632-645` | Voice choice needs a store built from scratch. |
| Replies are markdown | `SafeMarkdownRenderer` | Speaking the raw string reads fences and pipes aloud. A normalizer is mandatory. |
| Unsolicited replies bypass `SendMessage` | `BlazorUserFrontend.DisplayReplyAsync` | Scheduled/subagent results arrive on a bus thread; speaking them needs an event on `ChatStateService`. |

---

## Why the Web Speech API is out

The previous draft built on `SpeechRecognition` + `speechSynthesis`. Full duplex
rules both out, for three independent reasons — any one of them is disqualifying.

**`SpeechRecognition` opens the microphone itself.** There is no way to hand it
a `MediaStream`. That means there is no way to give it a stream captured with
`echoCancellation: true`, no way to run a VAD on the samples, and no way to know
what it is hearing until it decides to tell you. Full duplex requires owning the
capture path; the Web Speech API's entire design is that it owns it instead.

**`speechSynthesis` output sits outside the page's audio graph.** Browser
acoustic echo cancellation works by subtracting a reference signal — what is
being played — from what the mic hears. On iOS, `speechSynthesis` is routed
through the OS speech synthesiser rather than the page's render stream, so it
may never appear in that reference signal. The failure mode is RockBot
transcribing its own voice and answering itself. Playing TTS through a Web Audio
node in-page keeps it inside the AEC reference where it belongs.

**Neither can be interrupted cleanly mid-buffer.** `speechSynthesis.cancel()`
is only reliably responsive between utterances, which is why the half-duplex
draft had to chunk replies into ~200-character pieces. Barge-in needs to cut
audio within a few tens of milliseconds, not at the next sentence boundary.

The consequence is that **full duplex promotes server-side speech from "Phase 4,
optional" to "required"**. That is a real cost — a new secret, a new vendor, and
per-minute money. It also happens to fix the weakest part of the previous
design: the voice catalogue stops being device-specific, which makes the voice
picker meaningfully better (see "Choosing RockBot's voice").

### The speech-to-speech option, and why not

OpenAI's Realtime API and Gemini Live do full duplex natively — VAD,
interruption, and voice selection, over a single WebRTC connection the browser
can hold directly with an ephemeral token minted by the Blazor host. It is
genuinely the fastest path to a talking assistant.

It is the wrong path for RockBot, because those APIs *are the brain*. Using one
means the conversation happens inside a model that has no access to
`AgentLoopRunner` — no reasoning scaffolding, no completion evaluation, no
hallucination nudging, no context trimming, no tool-call metrics, and none of
RockBot's skills, memory, or tools. `CLAUDE.md` states that every LLM
tool-calling interaction must route through `RunAsync`; a speech-to-speech model
routes around all of it. What would be built is a different assistant that
happens to share a UI.

The realtime APIs can be used purely as an STT/TTS pipe with the model disabled,
but then the cost of a speech-to-speech model is being paid for a fraction of
it. A dedicated streaming STT provider plus a streaming TTS provider is cheaper,
swappable, and keeps RockBot's brain where it is.

---

## Architecture

```
Browser
  getUserMedia({ echoCancellation, noiseSuppression, autoGainControl })
        │
        ▼
  AudioWorklet ── 16 kHz PCM ──┬──► VAD (speech / not speech)
        │                      │
        │  gated by VAD        │  barge-in signal
        ▼                      ▼
  ┌──────────────── WebSocket  /voice/stream ────────────────┐
  │  up:   PCM frames, barge-in events                       │
  │  down: partial + final transcripts, TTS audio, control   │
  └──────────────────────────┬───────────────────────────────┘
        │                    │
        │  TTS audio ──► Web Audio node (in-page, so AEC sees it)
        ▼
Blazor host  (new: VoiceSessionHub)
  ├── ISpeechToText   ── streaming STT provider
  ├── ITextToSpeech   ── streaming TTS provider
  ├── SpeechTextRenderer ── markdown ─► speakable text
  └── UserProxyService ──► RabbitMQ ──► Agent
                                          │
                                   AgentLoopRunner
```

Two transports, deliberately: the SignalR circuit keeps doing UI state, and a
separate WebSocket carries audio. They must not share, both because of the 32 KB
receive cap and because a burst of audio frames should never delay a render.

The WebSocket terminates on the Blazor host rather than the browser talking to
the speech vendor directly. That keeps the provider key server-side, keeps the
vendor swappable behind `ISpeechToText` / `ITextToSpeech`, and puts the
transcript where `UserProxyService` already lives.

### Turn state machine

Full duplex means listening and speaking states overlap, so the machine is
described by two concurrent tracks rather than one ring.

```
  MIC TRACK      (always on while the session is open)
    Silent ──speech detected──► Speaking ──endpoint──► Transcribing ──► Silent
                                    │
                                    │ if RockBot is speaking AND
                                    │ sustained ≥ BargeInMs
                                    ▼
                              BARGE-IN ──► cancel playback
                                       ──► publish user.cancel
                                       ──► truncate spoken prefix

  AGENT TRACK
    Idle ──transcript──► Thinking ──first sentence──► Speaking ──► Idle
                            │                            ▲
                            └── spoken progress filler ───┘
```

Three details carry the experience:

- **Barge-in needs a threshold, not a trigger.** An open mic hears coughs, a
  television, and someone else in the room. Cutting RockBot off on the first
  frame of non-silence is maddening. Require sustained speech-like audio for
  `BargeInMs` (start around 300 ms) *and*, where the STT is fast enough, a
  partial transcript of more than a few characters before actually cancelling.
- **Barge-in must reach the agent.** Publishing to the existing
  `user.cancel.{agent}` topic stops the in-flight `RunAsync`. This path already
  exists and needs no agent change.
- **The spoken prefix must be what gets remembered.** See below — this is the
  subtle one.

### Interrupt truncation

When the user cuts in four seconds into a twenty-second answer, the agent's
conversation history must record **only the part that was actually spoken
aloud**. Otherwise RockBot believes it told the user things they never heard, and
every subsequent turn is built on a false premise — it will refer back to advice
it never delivered.

This needs a new message on the bus, `SpeechInterrupted { SessionId, SpokenPrefix }`,
and a handler that rewrites the stored assistant turn to the prefix plus an
explicit marker such as `…[interrupted by user]`. The client knows the prefix
precisely, because it knows exactly how many audio frames it played before
cutting the buffer.

This is the single most commonly skipped part of a full-duplex implementation and
the one that most degrades a long conversation. It is not a polish item.

---

## Closing the latency gap

Audio work alone leaves a multi-second silence after every question. Four
changes, in increasing order of cost, and the recommendation is to do the first
three.

**1. Speak the progress messages. (cheap, large effect)**
`ToolProgressNotifier` already publishes "Calling web_search…" and friends to the
bus, and they already reach the Blazor client via `DisplayStatusAsync`. In a
voice conversation these become exactly the filler a human would produce —
"let me look that up". This costs almost nothing and covers the majority of
perceived latency during tool-using turns. Phrasing needs a pass so the spoken
form is natural rather than a function name read aloud.

**2. A fast acknowledgement on the Low tier. (cheap, medium effect)**
The tier system already exists (`ModelTier.Low`). Fire a one-shot Low-tier call
in parallel with the real turn to produce a single spoken sentence — "sure, let
me check your calendar" — while Balanced/High does the work. Sub-second, and it
makes the gap conversational rather than dead.

**3. Stream the final answer only. (moderate cost, large effect)**
The tool loop itself cannot stream — it needs complete responses to parse tool
calls out of. But the *last* iteration, the one producing user-facing prose,
can. A streaming variant used only on that final call means RockBot starts
speaking roughly 400 ms after its last tool returns, instead of after the entire
answer has been generated. On a long answer this is the difference between a
four-second wait and a near-immediate one. The blast radius is contained to the
final-response path rather than all of `RunAsync`'s 2,800 lines.

**4. Full token streaming through `RunAsync`. (high cost)**
Deferred. It touches the mandatory chokepoint that every handler, subagent, and
A2A path routes through, and items 1–3 capture most of the benefit.

Sentence-level streaming is the right granularity throughout: TTS wants whole
clauses for correct prosody anyway, so there is nothing to gain from token-level
delivery to the speaker.

---

## Choosing RockBot's voice

Server-side TTS makes this straightforwardly better than it would have been.

The voice catalogue is now **device-independent** — the same list on the phone
and the desktop, the same voice on both. So the picker is a real list with a
preview button, populated from `ITextToSpeech.ListVoicesAsync()`, and the
selection is a genuine identity preference rather than a per-device
accommodation.

That means it should live with the agent, not in the browser. RockBot already
has an identity surface — `style.md`, the voice-and-tone document described in
`design/agent-identity.md` — and the speaking voice belongs beside it. Concretely:
persist the choice through the agent's user memory so it follows the user across
devices and clients, with `localStorage` used only as a fast local cache to avoid
a bus round-trip on page load.

Settings to expose: voice, speaking rate, barge-in sensitivity, and a
push-to-talk toggle for when an open mic is inappropriate — the single most
important control on the panel, and the one that must be reachable in one tap.

---

## Cost, privacy, and the always-open mic

Full duplex means a continuously open microphone streaming to a paid third-party
service. This is a material change in posture for a project whose stated core
principle is "nothing trusts the LLM", and it deserves to be designed rather
than discovered.

- **Gate the upstream on VAD.** Never stream silence. Client-side VAD means
  audio leaves the browser only when someone is actually talking, which cuts
  both cost and exposure by a large factor for a session left open.
- **Idle timeout.** Close the socket and release the mic after a few minutes of
  silence, with an obvious control to reopen. A tab forgotten overnight must not
  bill continuously.
- **Visible state.** A persistent, unambiguous indicator of whether the mic is
  open — not a subtle icon change. The user should never be unsure.
- **Explicit opt-in per session,** not a remembered "always on" setting.
- **No audio at rest.** Frames are relayed and dropped; nothing is written to
  the shared PVC. Only transcripts persist, and they persist exactly as typed
  messages already do.
- **Where the audio goes.** Streaming to a speech vendor is a genuine change in
  data flow from a system where the Blazor pod today holds nothing but a
  RabbitMQ password. It belongs in `design/security.md`, not only here.

---

## Provider choice

`ISpeechToText` and `ITextToSpeech` exist so this is a configuration decision,
not an architectural one. Requirements: streaming in both directions, WebSocket
or chunked transport, sub-300 ms time-to-first-audio for TTS, and interim
transcripts for barge-in detection.

Reasonable candidates on both sides include OpenAI, Azure Speech, Deepgram
(STT), and ElevenLabs (TTS). OpenAI is the least-new-vendor option given the
repo already speaks to OpenAI-compatible endpoints for LLM tiers; Azure Speech
covers both halves with one key and has the largest voice catalogue, which
matters for the picker.

The published latency numbers for these services move quickly and should not be
taken on trust from this document. Run a bake-off on time-to-first-audio and
interim-transcript latency before committing — those two numbers, more than
price, determine whether the conversation feels alive.

---

## Components

**New, agent side**
- `SpeechInterrupted` message + handler — truncate the stored assistant turn to
  the spoken prefix.
- Streaming final-response path in `AgentLoopRunner` (item 3 above).
- `ClientCapabilities.SpeechOutput = 1UL << 19` — bit 19 is free; 18
  (`ImageAttachment`) is the highest in use and the enum is documented as
  forward-compatible (`ClientCapabilities.cs:4-8`), so an agent that has never
  heard of the bit ignores it safely. Under full duplex this is set on every
  message and the agent-side prompt guidance becomes load-bearing rather than a
  nicety: short sentences, no tables, answer first and offer detail second.

**New, Blazor host**
- `VoiceSessionHub` — WebSocket endpoint at `/voice/stream`, one session per
  connection, owning the STT and TTS provider connections.
- `ISpeechToText` / `ITextToSpeech` + one implementation each.
- `SpeechTextRenderer` (`RockBot.UserProxy/Rendering/`) — markdown to speakable
  text, beside the existing `HtmlPlainTextRenderer`. Code fences become
  "code block, twelve lines — on screen"; tables become a row count; links
  become their text; emphasis markers, list bullets and emoji are dropped. Pure
  and unit-testable, in `RockBot.UserProxy` rather than the Blazor project so a
  future voice-note proxy can reuse it.
- `VoiceSettingsService` — agent-backed preference with a `localStorage` cache.

**New, browser**
- `voice.js` + an `AudioWorklet` processor — capture, downsample, VAD, WS
  framing, and Web Audio playback. Follows the existing `window.chatHelpers`
  convention (`App.razor:22`) as `window.rockbotVoice`.

**Changed**
- `Chat.razor` — mic control, session state, voice settings panel.
- `ChatStateService` — `OnSpeakableReply` event so unsolicited replies
  (scheduled tasks, subagent completions) can be spoken. Defaults to off; a
  phone announcing a background task at 3am is a bug report.

---

## Phasing

Ordered so each phase is independently useful and the riskiest unknown is
answered early.

| Phase | Scope | Delivers |
|---|---|---|
| **0** | `SpeechTextRenderer` + tests; provider bake-off on TTFA and interim latency | Pure code plus the numbers that decide whether the rest is worth building. |
| **1** | `VoiceSessionHub`, streaming TTS out, voice picker, settings | RockBot talks, in a chosen voice, same on every device. Half-duplex still. |
| **2** | `getUserMedia` + AudioWorklet + VAD + streaming STT in | The loop closes. Still turn-based, but hands-free. |
| **3** | **Full duplex**: open mic during playback, barge-in threshold, `user.cancel` on interrupt, `SpeechInterrupted` truncation | The actual ask. |
| **4** | Latency: spoken progress, Low-tier acknowledgement, streaming final answer | Makes it feel like a conversation rather than a fast walkie-talkie. |

Phase 4 is where the feature stops being a demo. It is tempting to reorder it
earlier; the argument against is that the barge-in and truncation work in Phase 3
is what determines whether long conversations stay coherent, and that is the
harder thing to retrofit.

### Degraded modes

- **No provider reachable / cost cap hit** → fall back to browser
  `speechSynthesis` for output and push-to-talk `SpeechRecognition` for input.
  This is the previous half-duplex design, retained as a fallback.
- **Firefox** → no `SpeechRecognition`, but the full-duplex path does not use it,
  so Firefox works *better* under this design than under the previous one.
- **Mic denied** → the existing text UI, unchanged.

---

## Testing

- `SpeechTextRendererTests` (`RockBot.UserProxy.Tests`) — table-driven over
  fences, tables, links, emoji, nested emphasis, origin-anchor blockquotes.
- Interrupt truncation — given a spoken prefix, the stored turn contains the
  prefix and the marker and nothing more. This is the correctness test that
  matters most; get it wrong and long conversations quietly rot.
- Barge-in thresholding — replay recorded audio fixtures (cough, background
  speech, genuine interruption) through the VAD and assert which trigger.
- `VoiceSessionHub` — provider interfaces mocked with Rocks, matching the
  existing `WorkIqAuthUiServiceTests` pattern.
- Echo cancellation cannot be unit tested and is the highest-risk unknown. It
  needs a manual matrix — iOS Safari, Android Chrome, desktop Chrome, desktop
  Safari — on **speakers, not headphones**, since headphones hide exactly the
  failure this design is trying to prevent. Run it at the end of Phase 3, and
  once informally during Phase 1 to catch a fundamental AEC problem before
  building on top of it.

## Open questions

1. **Does AEC actually hold on a phone at arm's length on speaker?** This is the
   load-bearing assumption of the whole design. If browser AEC cannot suppress
   RockBot's own voice on iOS Safari with the speaker at conversational volume,
   full duplex degrades to push-to-talk regardless of everything else here. It is
   worth a half-day spike with a throwaway page **before** Phase 1, because a
   negative answer changes the plan entirely.
2. **Is barge-in wanted during tool execution?** Interrupting spoken prose is
   clearly right. Interrupting "searching the web…" and killing a half-finished
   turn is less obviously right, and `user.cancel` does not distinguish them.
3. **How much of Phase 4 is needed before this is pleasant?** Possibly just
   spoken progress. Worth measuring after Phase 3 rather than assuming all three.
4. **Cost ceiling per session?** An open mic and a streaming TTS have no natural
   stopping point. A hard per-session cap with a spoken warning may be needed;
   deciding the number is a product call, not an engineering one.
