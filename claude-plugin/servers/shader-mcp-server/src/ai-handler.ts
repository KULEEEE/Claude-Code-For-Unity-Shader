import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";

interface AIRequest {
  prompt: string;
  shaderContext?: string;
  onChunk?: (chunk: string) => void;
  onStatus?: (status: string) => void;
}

interface AIResponse {
  success: boolean;
  response?: string;
  error?: string;
}

/**
 * Handle an AI query using the Claude Agent SDK.
 * Uses the existing Claude Code authentication — no API key needed.
 * Streams tokens in real-time via onChunk callback.
 */
export async function handleAIQuery(request: AIRequest): Promise<AIResponse> {
  const fullPrompt = buildFullPrompt(request.prompt, request.shaderContext);

  try {
    let resultText = "";

    request.onStatus?.("⏳ Claude Code 작업 시작...");

    for await (const msg of query({
      prompt: fullPrompt,
      options: { cwd: tmpdir() },
    })) {
      if (msg.type === "system") {
        request.onStatus?.("⏳ Claude Code 작업 중...");
      }

      // assistant message: extract text chunks + tool use status
      if (msg.type === "assistant") {
        const content = (msg as any).message?.content;
        if (Array.isArray(content)) {
          for (const block of content) {
            if (block.type === "text" && block.text) {
              request.onChunk?.(block.text);
            }
            if (block.type === "tool_use") {
              request.onStatus?.(`⚙️ ${block.name}`);
            }
          }
        }
      }

      // tool_progress events
      if (msg.type === "tool_progress") {
        const tp = msg as any;
        const elapsed = Math.round(tp.elapsed_time_seconds ?? 0);
        request.onStatus?.(`⚙️ ${tp.tool_name} (${elapsed}s)`);
      }

      // stream_event: tool use start
      if (msg.type === "stream_event") {
        const event = (msg as any).event;
        if (event?.type === "content_block_start" && event.content_block?.type === "tool_use") {
          request.onStatus?.(`⚙️ ${event.content_block.name}...`);
        }
        if (event?.type === "content_block_delta" && event.delta?.type === "text_delta") {
          request.onChunk?.(event.delta.text);
        }
      }

      // Final result
      if (msg.type === "result") {
        resultText = (msg as any).result ?? "";
      }
    }

    return { success: true, response: resultText };
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    return { success: false, error: msg };
  }
}

function buildFullPrompt(
  userPrompt: string,
  shaderContext?: string
): string {
  let prompt =
    "You are a Unity shader expert assistant. " +
    "Answer clearly and concisely. Use the user's language when possible. " +
    "When suggesting code changes, provide specific code snippets.\n\n";

  if (shaderContext) {
    prompt += `Shader Context:\n${shaderContext}\n\n`;
  }

  prompt += `User Question:\n${userPrompt}`;

  return prompt;
}
