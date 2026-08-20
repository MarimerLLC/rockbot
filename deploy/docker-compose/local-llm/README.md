# RockBot (vanilla) on the local Qwen3.6 GPU

A stock RockBot pointed at the llama.cpp server running on this Windows host.

Compose project name is **`rockbot-local`**, deliberately distinct from the
`rockbot` project in the parent directory — that one is the heavily-customised
Grok storytelling agent. Nothing here touches it: separate project, separate
volumes, separate ports, separate `agent-data`. Both can run at once.

## What "vanilla" means here

`agent-data/` starts empty, so the init container seeds the **stock** profile
(`directives.md`, `soul.md`, `memory-rules.md`, `style.md`, dream prompts …)
straight from the image. No prompt has been edited. The only deviations from
framework defaults are in `agent.extra.env`, and every one of them is there to
make a 32K-context local reasoning model work — each is commented with the
measurement that motivated it.

To get back to a virgin agent: `docker compose ... down` then `rm -rf agent-data`.

## Ports

| Service | Host port | Why |
|---|---|---|
| llama-server (not in this stack) | 8080 | pre-existing, do not disturb |
| Blazor UI | **8090** | 8080 collides with llama-server |
| RabbitMQ AMQP | **5673** | 5672 belongs to the `rockbot` stack |
| RabbitMQ management | **15673** | 15672 belongs to the `rockbot` stack |

## Run

```bash
# 0. The LLM server is started BY HAND and does not survive a reboot.
curl -s http://127.0.0.1:8080/health          # expect {"status":"ok"}
# Restart procedure: S:\src\gpu\LLM-ENDPOINT-FOR-DOCKER.md

cd /s/src/rdl/rockbot
docker compose -f deploy/docker-compose/local-llm/docker-compose.yml \
  --env-file deploy/docker-compose/local-llm/.env up -d --build
```

UI: <http://localhost:8090>

CLI against this stack (note the non-default port):

```bash
RabbitMq__HostName=localhost RabbitMq__Port=5673 \
RabbitMq__UserName=rockbot RabbitMq__Password=rockbot \
  dotnet run --project src/RockBot.UserProxy.Cli -- chat
```

## Verified working (2026-08-19)

Probed against the live server before and after bringing the stack up:

- **Native tool calling** — real `tool_calls` with valid JSON arguments. No
  text-based tool-call fallback needed, unlike Euryale/Hermes in the sibling stack.
- **End-to-end turn** — `SaveMemory` and `list_scheduled_tasks` both dispatched
  and folded into a correct final reply.
- **Reasoning isolation** — thinking goes to `reasoning_content`, so no `<think>`
  blocks leak into replies. Confirmed: content came back as exactly `"OK"`.
- **Prompt caching** — llama.cpp reuses the KV prefix; a repeated turn showed
  98.9% cached input, so the second tool-loop iteration is nearly free.
- **All three tiers** resolve to `qwen3.6 @ http://host.docker.internal:8080/v1`.
- **Container → host networking** works with no Windows Firewall rule.

## Context: the server was raised to 64K, and why

A stock turn's single-request prompt is **~24,800 tokens** — the system prompt
plus 24 tool schemas. That floor does not shrink as the chat grows, and the
knobs in `agent.extra.env` bound tool *results*, not the floor. Against the
original 32,768-token window that left ~4K of usable headroom, so llama-server
was restarted on 2026-08-19:

```
-ngl 99 -ncmoe 5 -fa on -c 65536      # was -ncmoe 3 ... -c 32768
```

Measured after the restart:

| | before | after |
|---|---|---|
| Context window | 32,768 | **65,536** |
| Headroom above the ~24.8K floor | ~8,000 | **~40,700** |
| Generation throughput | ~140 tok/s | **117.2 tok/s** |
| VRAM | 15,964 / 16,303 MiB | 15,940 / 16,303 MiB |

`-ncmoe` **must** rise with the context — KV cache competes with weights for
VRAM. Too low spills VRAM to system RAM and costs ~85% throughput with **no
error message**; the 117 tok/s measurement above is the check that this did not
happen (a spill reads as ~20 tok/s). The full table is in
`S:\src\gpu\LLM-ENDPOINT-FOR-DOCKER.md`.

Note the server runs `-np` auto with `kv_unified=true`, so `-c` is one shared KV
buffer across the 4 slots, not a per-slot allocation.

### Reading the token logs

`input=50652` in the agent log is Microsoft.Extensions.AI **summing usage across
tool-loop iterations**, not one oversized request — which is why it is followed
by a high `cached=` percentage. Divide by the iteration count for the real
per-request size.

### Overflow fails loudly

Verified by deliberately sending a 40K prompt to the 32K server:

```
400 exceed_context_size_error: request (40035 tokens) exceeds the
available context size (32768 tokens), try increasing it
```

So if the window is ever exceeded you get a visible error, not a silent
truncation of the start of the context.

## Watch items

- **Speed.** ~117 tok/s generation on one shared GPU. A single user turn is tens
  of seconds to a couple of minutes because every tool-loop iteration re-runs a
  reasoning block over a ~25K prompt. `LlmCallTimeout` is raised to 4 minutes
  (the HTTP `NetworkTimeout` is pinned at 5 minutes in `Program.cs`, so 4 is the
  practical ceiling if the timeout is to stay attributable).
- **Reasoning burn.** Observed 1,300–2,600 completion tokens on ordinary turns,
  almost all reasoning. This is why `MaxOutputTokens=6144`; a low cap returns
  `finish_reason=length` with empty `content` and **no error**, which looks
  exactly like a broken model.
- **Date fabrication (intermittent).** On one early turn the model invented
  "Thursday, May 21, 2025" instead of reading the injected datetime system
  message; a later turn on the same stack got it right. In an isolated two-message
  prompt it is always correct, so this is attention dilution in a ~25K prompt, not
  a wiring bug — the container clock and `AgentContextBuilder` injection were both
  verified correct. If it recurs often, add a
  `model-behaviors/qwen3/additional-system-prompt.md` restating the date rule.
- **Don't start Ollama.** VRAM is at ~97%. A second GPU model makes the driver
  page VRAM to RAM and throughput drops ~8x with no error message.
