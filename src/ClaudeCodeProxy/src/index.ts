import "express-async-errors";
import express, { Request, Response, NextFunction } from "express";
import { resolveAuth } from "./auth.js";
import { startTokenRefreshScheduler } from "./tokenRefresh.js";
import {
  OpenAiRequest,
  AnthropicResponse,
  toAnthropicRequest,
  toOpenAiResponse,
} from "./translate.js";

const PORT = parseInt(process.env.PORT ?? "8080", 10);
const ANTHROPIC_API_BASE =
  process.env.ANTHROPIC_API_BASE ?? "https://api.anthropic.com";
const ANTHROPIC_VERSION = "2023-06-01";
const DEFAULT_MODEL = process.env.DEFAULT_MODEL ?? "claude-sonnet-4-6";

const app = express();
app.use(express.json({ limit: "10mb" }));

// ── Health ────────────────────────────────────────────────────────────────────

app.get("/health", (_req, res) => {
  res.json({ status: "ok" });
});

// ── Models list (minimal — lets RockBot discover the proxy) ───────────────────

app.get("/v1/models", (_req, res) => {
  res.json({
    object: "list",
    data: [
      { id: DEFAULT_MODEL, object: "model", created: 0, owned_by: "anthropic" },
      { id: "claude-opus-4-6", object: "model", created: 0, owned_by: "anthropic" },
      { id: "claude-haiku-4-5", object: "model", created: 0, owned_by: "anthropic" },
    ],
  });
});

// ── Chat completions ──────────────────────────────────────────────────────────

app.post("/v1/chat/completions", async (req: Request, res: Response) => {
  const body = req.body as OpenAiRequest;

  if (!body.messages || !Array.isArray(body.messages)) {
    res.status(400).json({ error: { message: "messages array is required", type: "invalid_request_error" } });
    return;
  }

  // Streaming is not yet implemented — tell the caller to use non-streaming
  if (body.stream) {
    res.status(422).json({
      error: {
        message: "Streaming is not supported by this proxy. Set stream=false.",
        type: "invalid_request_error",
      },
    });
    return;
  }

  const model = body.model || DEFAULT_MODEL;
  const anthropicRequest = toAnthropicRequest({ ...body, model });

  let auth: ReturnType<typeof resolveAuth>;
  try {
    auth = resolveAuth();
  } catch (err) {
    res.status(500).json({ error: { message: (err as Error).message, type: "auth_error" } });
    return;
  }

  const url = `${ANTHROPIC_API_BASE}/v1/messages`;

  console.log(
    `→ ${model} [${auth.source}] tools=${anthropicRequest.tools?.length ?? 0} msgs=${anthropicRequest.messages.length}`
  );

  const upstream = await fetch(url, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "anthropic-version": ANTHROPIC_VERSION,
      [auth.header]: auth.value,
    },
    body: JSON.stringify(anthropicRequest),
  });

  const responseText = await upstream.text();

  if (!upstream.ok) {
    console.error(`← Anthropic ${upstream.status}: ${responseText}`);
    // Relay the Anthropic error in OpenAI error envelope
    let detail: unknown;
    try { detail = JSON.parse(responseText); } catch { detail = responseText; }
    res.status(upstream.status).json({ error: detail });
    return;
  }

  const anthropicResponse = JSON.parse(responseText) as AnthropicResponse;
  const openAiResponse = toOpenAiResponse(anthropicResponse, model);

  console.log(
    `← ${upstream.status} stop=${anthropicResponse.stop_reason} ` +
    `in=${anthropicResponse.usage.input_tokens} out=${anthropicResponse.usage.output_tokens}`
  );

  res.json(openAiResponse);
});

// ── Error handler ─────────────────────────────────────────────────────────────

app.use((err: Error, _req: Request, res: Response, _next: NextFunction) => {
  console.error("Unhandled error:", err);
  res.status(500).json({ error: { message: err.message, type: "internal_error" } });
});

// ── Start ─────────────────────────────────────────────────────────────────────

app.listen(PORT, () => {
  let authSource = "unknown";
  try { authSource = resolveAuth().source; } catch { authSource = "MISSING — set ANTHROPIC_API_KEY or run claude auth login"; }
  console.log(`claude-code-proxy listening on :${PORT}`);
  console.log(`  default model : ${DEFAULT_MODEL}`);
  console.log(`  auth source   : ${authSource}`);
  console.log(`  anthropic api : ${ANTHROPIC_API_BASE}`);
  startTokenRefreshScheduler();
});
