# fallback-image-probe

A live check that `FallbackChatClient` routes image requests only to models that can read them (#818). It uses the real `FallbackChatClient` and real providers, but not the agent, the bus or the cluster. It makes 3 small paid calls and is not part of the solution, so CI never runs it.

Unit tests cover the same logic with stubs (`tests/RockBot.Llm.Tests/FallbackChatClientImageRoutingTests.cs`). This probe checks what stubs can't: real provider errors classify as expected, and a real model reads the image.

## What it does

It builds a three-entry chain:

| Entry | Configured as | Expected |
|---|---|---|
| `[0]` Anthropic API, **deliberately invalid key** | reads images | 401, marked degraded, fails over |
| `[1]` OpenAI-compatible model (default `gpt-5.6-terra`) | **flagged text-only** | skipped for the image; answers the text request |
| `[2]` Anthropic Messages API on OpenRouter (default `anthropic/claude-haiku-5.5`) | reads images | answers the image |

Then it sends:
1. **An image request** with `vision-test.png`: a red circle, blue square, green triangle, and the text `CODE 7Q4X`.
2. **A text request** on the same chain.

It prints `PASS`/`FAIL` for each check and exits non-zero if any fail.

Pick a `[1]` model that **can** see. If the skip were broken it would answer the image and the probe would catch it. A genuinely blind model would answer with a 400 instead, which also ends the request, so a broken skip would look like a provider error.

## Running it

**Against a live deployment** (reads keys from the LLM secret and prints none of them):

```powershell
./tools/fallback-image-probe/Invoke-Probe.ps1
./tools/fallback-image-probe/Invoke-Probe.ps1 -Namespace rockbot -Secret rockbot-secrets -TextOnlyModel gpt-5.6-terra
```

The script takes the first OpenRouter entry and the first other OpenAI-compatible entry from `LLM__BalancedModels__<n>__*` by matching endpoints, so it works whatever order the chain is in. `-TextOnlyModel` must be a model served by that OpenAI-compatible endpoint.

**With your own keys:**

```powershell
$env:PROBE_OPENROUTER_KEY = '...'
$env:PROBE_TEXTONLY_KEY = '...'
$env:PROBE_TEXTONLY_ENDPOINT = 'https://<resource>.cognitiveservices.azure.com/openai/v1/'
dotnet run tools/fallback-image-probe/probe.cs
```

Optional overrides: `PROBE_ANTHROPIC_MODEL`, `PROBE_TEXTONLY_MODEL`, `PROBE_OPENROUTER_MODEL`, and a different image path as the first argument (after `--`). With a different image, change `ImageCode` in `probe.cs` to text that appears in it.

`probe.cs` is a .NET file-based app, so it needs the .NET 10 SDK that `global.json` pins. It turns off the AOT-friendly defaults that file-based apps get, because the Anthropic SDK needs reflection-based JSON.

## Expected output

```
=== 1. image request ===
  -> calling claude-haiku-5-5 (Anthropic API, invalid key)
  [Warning] FallbackChatClient: model claude-haiku-5-5 marked degraded (HardError, matched status 401); ...
  [OnFallback] claude-haiku-5-5 -> anthropic/claude-haiku-5.5 (HardError)
  [Information] FallbackChatClient: skipping gpt-5.6-terra for this request: it carries image content ...
  -> calling anthropic/claude-haiku-5.5 (OpenRouter)
  PASS  the broken primary was tried once and failed over
  PASS  the text-only model was skipped for the image
  PASS  the image went to the next model that declares image input
  PASS  the reply read the image (contains 7Q4X)
=== 2. text request on the same chain ===
  -> calling gpt-5.6-terra (flagged text-only)
  PASS  the text-only model answered the text request
  PASS  the text request did not need the last model
=== all checks passed ===
```
