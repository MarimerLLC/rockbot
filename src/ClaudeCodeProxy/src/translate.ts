/**
 * Bidirectional translation between OpenAI chat completions format and
 * Anthropic Messages API format.
 *
 * OpenAI reference:  https://platform.openai.com/docs/api-reference/chat
 * Anthropic reference: https://docs.anthropic.com/en/api/messages
 */

// ── OpenAI types (inbound) ────────────────────────────────────────────────────

export interface OpenAiMessage {
  role: "system" | "user" | "assistant" | "tool";
  content: string | OpenAiContentPart[] | null;
  tool_calls?: OpenAiToolCall[];
  tool_call_id?: string;
  name?: string;
}

export interface OpenAiContentPart {
  type: "text" | "image_url";
  text?: string;
  image_url?: { url: string };
}

export interface OpenAiToolCall {
  id: string;
  type: "function";
  function: { name: string; arguments: string };
}

export interface OpenAiTool {
  type: "function";
  function: {
    name: string;
    description?: string;
    parameters?: unknown;
  };
}

export interface OpenAiRequest {
  model: string;
  messages: OpenAiMessage[];
  tools?: OpenAiTool[];
  tool_choice?: unknown;
  max_tokens?: number;
  temperature?: number;
  stream?: boolean;
  stop?: string | string[];
}

// ── Anthropic types (outbound) ────────────────────────────────────────────────

interface AnthropicTextContent {
  type: "text";
  text: string;
}

interface AnthropicImageContent {
  type: "image";
  source: { type: "base64"; media_type: string; data: string } | { type: "url"; url: string };
}

interface AnthropicToolUseContent {
  type: "tool_use";
  id: string;
  name: string;
  input: unknown;
}

interface AnthropicToolResultContent {
  type: "tool_result";
  tool_use_id: string;
  content: string;
}

type AnthropicUserContent =
  | AnthropicTextContent
  | AnthropicImageContent
  | AnthropicToolResultContent;

type AnthropicAssistantContent =
  | AnthropicTextContent
  | AnthropicToolUseContent;

interface AnthropicMessage {
  role: "user" | "assistant";
  content: string | AnthropicUserContent[] | AnthropicAssistantContent[];
}

export interface AnthropicRequest {
  model: string;
  system?: string;
  messages: AnthropicMessage[];
  tools?: AnthropicTool[];
  max_tokens: number;
  temperature?: number;
  stop_sequences?: string[];
}

interface AnthropicTool {
  name: string;
  description?: string;
  input_schema: unknown;
}

// ── Anthropic response types ──────────────────────────────────────────────────

interface AnthropicResponseTextBlock {
  type: "text";
  text: string;
}

interface AnthropicResponseToolUseBlock {
  type: "tool_use";
  id: string;
  name: string;
  input: unknown;
}

type AnthropicResponseContent =
  | AnthropicResponseTextBlock
  | AnthropicResponseToolUseBlock;

export interface AnthropicResponse {
  id: string;
  type: "message";
  role: "assistant";
  model: string;
  content: AnthropicResponseContent[];
  stop_reason: "end_turn" | "tool_use" | "max_tokens" | "stop_sequence";
  usage: { input_tokens: number; output_tokens: number };
}

// ── Translation: OpenAI → Anthropic ──────────────────────────────────────────

function messageContentToString(content: OpenAiMessage["content"]): string {
  if (!content) return "";
  if (typeof content === "string") return content;
  return content
    .filter((p): p is OpenAiContentPart & { text: string } => p.type === "text" && !!p.text)
    .map((p) => p.text)
    .join("\n");
}

export function toAnthropicRequest(req: OpenAiRequest): AnthropicRequest {
  const systemMessages = req.messages.filter((m) => m.role === "system");
  const system = systemMessages.map((m) => messageContentToString(m.content)).join("\n\n") || undefined;

  const anthropicMessages: AnthropicMessage[] = [];

  for (const msg of req.messages) {
    if (msg.role === "system") continue;

    if (msg.role === "user") {
      anthropicMessages.push({
        role: "user",
        content: messageContentToString(msg.content),
      });
    } else if (msg.role === "assistant") {
      if (msg.tool_calls && msg.tool_calls.length > 0) {
        // Assistant message with tool calls → mixed content
        const content: AnthropicAssistantContent[] = [];
        const text = messageContentToString(msg.content);
        if (text) content.push({ type: "text", text });
        for (const tc of msg.tool_calls) {
          let input: unknown = {};
          try { input = JSON.parse(tc.function.arguments); } catch { /* keep empty */ }
          content.push({ type: "tool_use", id: tc.id, name: tc.function.name, input });
        }
        anthropicMessages.push({ role: "assistant", content });
      } else {
        anthropicMessages.push({
          role: "assistant",
          content: messageContentToString(msg.content),
        });
      }
    } else if (msg.role === "tool") {
      // Tool result — must follow the assistant message that requested it
      // Anthropic batches all tool results into a single user message
      const last = anthropicMessages[anthropicMessages.length - 1];
      const toolResult: AnthropicToolResultContent = {
        type: "tool_result",
        tool_use_id: msg.tool_call_id ?? "",
        content: messageContentToString(msg.content),
      };
      if (last?.role === "user" && Array.isArray(last.content)) {
        (last.content as AnthropicUserContent[]).push(toolResult);
      } else {
        anthropicMessages.push({ role: "user", content: [toolResult] });
      }
    }
  }

  const tools: AnthropicTool[] | undefined = req.tools?.map((t) => ({
    name: t.function.name,
    description: t.function.description,
    input_schema: t.function.parameters ?? { type: "object", properties: {} },
  }));

  return {
    model: req.model,
    system,
    messages: anthropicMessages,
    tools: tools?.length ? tools : undefined,
    max_tokens: req.max_tokens ?? 8192,
    temperature: req.temperature,
    stop_sequences: Array.isArray(req.stop) ? req.stop : req.stop ? [req.stop] : undefined,
  };
}

// ── Translation: Anthropic → OpenAI ──────────────────────────────────────────

export function toOpenAiResponse(res: AnthropicResponse, requestedModel: string) {
  const textContent = res.content
    .filter((b): b is AnthropicResponseTextBlock => b.type === "text")
    .map((b) => b.text)
    .join("\n");

  const toolUseBlocks = res.content.filter(
    (b): b is AnthropicResponseToolUseBlock => b.type === "tool_use"
  );

  const toolCalls: OpenAiToolCall[] | undefined =
    toolUseBlocks.length > 0
      ? toolUseBlocks.map((b) => ({
          id: b.id,
          type: "function" as const,
          function: {
            name: b.name,
            arguments: JSON.stringify(b.input),
          },
        }))
      : undefined;

  const finishReason =
    res.stop_reason === "tool_use"
      ? "tool_calls"
      : res.stop_reason === "end_turn"
      ? "stop"
      : res.stop_reason === "max_tokens"
      ? "length"
      : "stop";

  return {
    id: res.id,
    object: "chat.completion",
    created: Math.floor(Date.now() / 1000),
    model: requestedModel,
    choices: [
      {
        index: 0,
        message: {
          role: "assistant",
          content: textContent || null,
          tool_calls: toolCalls,
        },
        finish_reason: finishReason,
      },
    ],
    usage: {
      prompt_tokens: res.usage.input_tokens,
      completion_tokens: res.usage.output_tokens,
      total_tokens: res.usage.input_tokens + res.usage.output_tokens,
    },
  };
}
