import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

interface AIRequest {
  prompt: string;
  shaderContext?: string;
  language?: string;
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
  const fullPrompt = buildFullPrompt(request.prompt, request.shaderContext, request.language);

  try {
    let resultText = "";

    request.onStatus?.("⏳ Claude Code 작업 시작...");

    // Resolve path to this MCP server's entry point for the Agent SDK
    const serverPath = join(dirname(fileURLToPath(import.meta.url)), "server.mjs");

    for await (const msg of query({
      prompt: fullPrompt,
      options: {
        cwd: tmpdir(),
        permissionMode: "bypassPermissions",
        allowDangerouslySkipPermissions: true,
        mcpServers: {
          "unity-shader-tools": {
            command: "node",
            args: [serverPath],
          },
        },
      },
    })) {
      if (msg.type === "system") {
        request.onStatus?.("⏳ Claude Code 작업 중...");
      }

      // assistant message: show tool use status (text comes from result)
      if (msg.type === "assistant") {
        const content = (msg as any).message?.content;
        if (Array.isArray(content)) {
          for (const block of content) {
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
  shaderContext?: string,
  language?: string
): string {
  let prompt =
    "You are a Unity shader expert assistant. " +
    "Answer clearly and concisely. " +
    "When suggesting code changes, provide specific code snippets.\n";

  if (language) {
    prompt += `IMPORTANT: You MUST respond in ${language}.\n`;
  } else {
    prompt += "Use the user's language when possible.\n";
  }

  prompt += "\n";

  if (shaderContext) {
    prompt += `Shader Context:\n${shaderContext}\n\n`;
  }

  prompt += `User Question:\n${userPrompt}`;

  return prompt;
}
