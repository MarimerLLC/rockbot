# Multimodal input

## Why

RockBot has been text-only end to end. Every model it talks to can see, but nothing in the
framework could put a non-text byte in front of one. Issue #513 arrived at this from the
outside: an agent asked an MCP server for an image, got 167K characters of textual
representation back, chunked it into working memory, and never showed the model an image.

The model was vision-capable. The image never reached it *as an image*.

This document is the contract for closing that gap: what was missing, what the wire formats
actually permit, and the order the pieces land in.

## What was missing

Five separate things, verified in the tree before any of this was written:

1. **Every internal contract is text-only.** `UserMessage.Content`, `ConversationTurn.Content`,
   and `LlmChatMessage.Content` are all `string`. `LlmMessageMapper.ToChatMessages` can only
   build `TextContent`, `FunctionCallContent`, and `FunctionResultContent`. The one exception
   is `ILlmClient.GetResponseAsync`, which takes M.E.AI `ChatMessage` — and `ChatMessage` has
   supported `DataContent` all along. The capability exists at the boundary that matters;
   nothing upstream could reach it.

2. **`file_read` is `File.ReadAllTextAsync`.** A PNG comes back as mojibake. No MIME
   awareness, no binary detection, no size guard.

3. **The tool-result image path is half-built** — and on the wire format RockBot speaks, it
   cannot be finished. See below.

4. **No model capability declaration.** `ModelBehavior` carries a dozen behavioural flags and
   not one modality flag, so nothing could tell a seeing tier from a blind one.

5. **The context-budget machinery is blind to bytes.** ~~`EstimateMessageChars` counts
   `TextContent` and `FunctionResultContent` by length and everything else at a flat 50 —
   so a `DataContent` carrying a 1.8 MB image counts as 50 characters, roughly 35,000× under.
   Images are effectively invisible to the watermark trim and to every stash decision.~~
   **Closed (issue #564).** See [below](#sizing-an-image-pixel-dimensions-not-byte-count).

## The constraint: images cannot ride in a tool result

`RegistryToolFunction.ToAIContent` already maps MCP image and audio blocks to `DataContent`,
and `McpToolExecutor.MapContentBlocks` already carries `ImageContentBlock` across the bus.
That looks like a working path. It is not, for two independent reasons.

**On the text-based tool-calling path**, `AgentLoopRunner` reduces the result with
`result?.ToString()`. A `List<AIContent>` stringifies to a bare type name. The image isn't
degraded, it's annihilated.

**On the FICC path** the list survives — `ChunkingAIFunction` returns the object unchanged
when its `ToString()` is under the threshold — and reaches M.E.AI intact. But every provider
RockBot talks to is reached through `OpenAIClient(...).GetChatClient(...).AsIChatClient()`,
and the OpenAI Chat Completions wire format accepts **only text in tool-role messages**. A
`DataContent` there is JSON-serialised into a data-URI string: the full base64 token cost,
and no image.

So: **on OpenAI-compatible APIs, bytes can only enter as content parts on a user or system
message.** Any design that hands the agent loop an image *as a tool return value* is dead on
arrival regardless of the model's vision. This is the single most important fact in this
document, and it is what shapes the ordering below.

## Architecture

Two concerns, kept separate — the issue's own framing, and it is the right one:

```
1.  External system (repo, mailbox, MCP server, script)
              |  materialise
              v
        /rockbot/shared/...          <- mostly solved: attachment gateway, scripts, file tools

2.  /rockbot/shared/file.png
              |  hand to a model AS BYTES
              v
        configured RockBot LLM tier  <- this document
```

Concern (2) is served by a **side call**, not by enriching the main loop's tool results:

```
Agent: analyze_file({ path: "diagrams/arch.png", prompt: "Describe the components.", tier: "High" })
    |
    v
AnalyzeFileToolExecutor
    |   SafeResolvePath containment check under FileSystemOptions.BasePath
    |   extension -> MIME, checked against the allowlist
    |   size checked against AnalyzeFileMaxBytes
    |   File.ReadAllBytesAsync
    |
    v
ILlmClient.GetResponseAsync(
    [ ChatMessage(User, [ TextContent(prompt), DataContent(bytes, mime) ]) ],
    tier, options: null, ct)
    |
    v
ToolInvokeResponse { Content = response.Text }    <- text only; bytes never enter the agent loop
```

The agent reasons in paths. Bytes never cross the message bus, never enter the conversation
history, and never count against the context budget — which is why gap (5) can wait.

## Key design decisions

### A side call, not a richer tool result

The obvious-looking fix — let a tool return an image and let the loop forward it — cannot
work on OpenAI-compatible APIs, as above. A side call also gets three things for free that
the inline design would have had to solve: the bytes never touch context, the budget
machinery needs no changes, and the failure mode when a model cannot see is a sentence from
one tool rather than a 400 from the middle of a loop.

The cost is that the analysis is one-shot: the main model sees a description, not the image,
and cannot go back and look again without another call. That is the correct trade for now.
Inline images belong to concern (D) below, where a user attaches one to a message.

### The modality flag lives on the tier config, not on `ModelBehavior`

`ModelBehavior` describes how a model *behaves* — whether it hallucinates tool calls, whether
it needs text-based tool calling. Whether a model can see is a property of the configured
model itself, which is `LlmTierConfig`. It is a `bool SupportsImageInput` rather than a
modality set: audio and PDF differ per provider in ways we cannot usefully model yet, and a
second bool is cheap to add when a deployment actually needs one.

It defaults to `false`. A tool that silently sends bytes to a blind model produces an opaque
provider error deep in the stack; making the operator say "this model can see" is one config
line and removes an entire class of confusing failure.

### `analyze_file` is not registered unless a tier declares vision

Registering a tool the deployment cannot execute teaches the model a capability it will then
try to use. When no tier sets `SupportsImageInput`, the registrar logs at startup and
registers nothing.

### Tier selection prefers a seeing tier over the requested one

`LlmClient` falls back to Balanced whenever a Low or High call throws. If the requested tier
can see but Balanced cannot, that fallback lands a vision request on a blind model. So the
executor resolves the requested tier against the vision-capable set first and substitutes the
nearest capable tier (High → Balanced → Low) with a warning, rather than sending the call
somewhere it will fail. When *no* tier can see, the tool is never registered in the first
place.

### It lives in `RockBot.Tools.FileSystem`

That package already references `RockBot.Host` (so `ILlmClient` is in scope), already owns
`SafeResolvePath` containment against the shared volume, and already defaults its base path
to `/rockbot/shared` — which means the MCP attachment gateway's output directory is *already*
inside its reachable scope. A file materialised by the gateway can be analysed with no
further plumbing.

### `LlmTierOptions` becomes a registered service

It was previously a local in each host's `Program.cs`, bound and then handed to client
factories. The executor needs to know which tiers can see, so the agent registers the bound
instance as a singleton. Consumers that do not register it get an `analyze_file` that never
registers — the dependency is optional, and its absence reads the same as "no tier declares
vision".

### Sizing an image: pixel dimensions, not byte count

`AgentLoopRunner.EstimateContentChars` now models `DataContent`, `FunctionCallContent`,
`TextReasoningContent`, `UriContent` and `ErrorContent` instead of charging them a flat 50.
Non-image binary content (audio, PDF) is sized by its base64 wire cost, 4 chars per 3 bytes.
Images are sized from their **pixel dimensions**, by `ImageTokenEstimator`.

Bytes are the wrong unit for an image, in both magnitude and ordering. The provider scales the
image into a bounded tile grid and charges a flat base plus a fixed cost per tile — so a 4 MB
photo and a 400 KB screenshot of the same dimensions cost exactly the same, and a 5 KB icon
costs a fraction of either. A byte proxy over-charges the icon by more than an order of
magnitude, and once capped (the ceiling has to be low enough not to blow the budget) every real
photo and screenshot pins to that same ceiling — which makes the proxy inert precisely where it
was meant to help.

So `ImageTokenEstimator` reads width and height from the image header — PNG, JPEG, GIF, WebP and
BMP, all of which carry it in the first few dozen bytes; nothing decodes pixels — and applies
the scale-then-tile cost model: fit inside 2048×2048, bring the shortest side down to 768
(down-only, never upscaled), tile at 512px, charge `85 + 170 × tiles`. That reproduces the
provider's own worked examples (1024×1024 → 765 tokens; 2048×4096 → 1,105) and is bounded at
`MaxTokens` = 1,445, since the scaling rules cannot yield more than eight tiles.

`MaxImageChars` is derived from that bound rather than guessed: an image whose header will not
parse is charged what the *largest* possible image would cost, because an image we cannot
measure could be that large. Three smaller consequences worth knowing: an image part with no
readable payload is charged the ceiling rather than zero — a degenerate image is a malformed
request, not a free one; an unparseable header is logged once per media type at debug; and the
unknown-content fallback increments `rockbot.agent.context.unknown_content_part`, tagged with
the CLR type name, and logs once per type. A wrong-but-quiet default is what made this gap easy
to miss for so long, so neither approximation is silent any more.

## Configuration

```json
{
  "LLM": {
    "High": {
      "ModelId": "openai/gpt-5.5",
      "SupportsImageInput": true
    }
  }
}
```

Or as an environment variable: `LLM__High__SupportsImageInput=true`.

`FileSystemOptions` gains two knobs, both with defaults that need no attention:

- `AnalyzeFileMaxBytes` (default 8 MiB) — refused above this, before any bytes are read into
  a request. Providers cap the encoded request well above this; the limit exists to keep a
  mistake cheap.
- `AnalyzeFileMimeTypes` — the allowlist, defaulting to PNG, JPEG, GIF, and WebP: the four
  formats every vision-capable provider accepts. Adding `application/pdf` or an audio type is
  a deployment decision, because whether it works depends on the provider.

## Sequencing

- **(A) `analyze_file` + (B) the modality flag** — this PR. Unblocks #513's actual use case
  with no persisted-schema churn.
- **(C) Generic binary capture in the MCP bridge** — done, as `BinaryResponseCapture`. Typed
  image and audio content blocks are stashed to the attachments directory and replaced with
  `{path, name, size, mime}` with no configuration at all; the per-server `attachments`
  manifest gained declarative *response* extraction (which field holds the base64, which holds
  the name) so a server returning `{content, encoding: "base64"}` — Gitea's shape — is adapted
  without server cooperation. A binary test keeps text files from being captured out of the
  response. This was the direct fix for the 167K-character chunk storm, and it feeds (A). See
  [`mcp-attachments.md`](mcp-attachments.md#binary-capture--the-fallback-for-servers-that-never-heard-of-us).
- **(D) Inbound user attachments** (issue #565, blocked on #564) — `UserMessage.Attachments` as path references mirroring
  `AgentAttachment`, a Blazor upload writing into the shared directory, `ConversationTurn`
  extended, and the loop injecting `DataContent` onto the user message. This one touches bus
  contracts, the UI, and the conversation store. Adding an optional `Attachments` property to
  `ConversationTurn` is additive, so by the policy in `schema-migrations.md` it needs no
  migration — and the conversation store is not enrolled in schema migrations at all, unlike
  memory, skills, feedback and wisp. What it does need is gap (5) — issue #564 — fixed
  first, or the trim logic silently miscounts every image.

Video is out of scope. Only Gemini-family models accept it natively and RockBot is
OpenAI-compatible end to end. Audio and PDF are not separate features — they are entries in
the same MIME allowlist on the same `DataContent` path, and become available as soon as a
tier declares support for them.
