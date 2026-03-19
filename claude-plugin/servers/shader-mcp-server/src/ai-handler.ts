import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";
import { existsSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

interface AIRequest {
  prompt: string;
  context?: string;
  language?: string;
  projectPath?: string;
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
  const fullPrompt = buildFullPrompt(
    request.prompt,
    request.context,
    request.language,
    request.projectPath
  );

  // Use Unity project path as cwd if available, fallback to tmpdir
  const cwd =
    request.projectPath && existsSync(request.projectPath)
      ? request.projectPath
      : tmpdir();

  try {
    let resultText = "";

    request.onStatus?.("⏳ Claude Code 작업 시작...");

    // Resolve path to this MCP server's entry point for the Agent SDK
    const serverPath = join(
      dirname(fileURLToPath(import.meta.url)),
      "server.mjs"
    );

    for await (const msg of query({
      prompt: fullPrompt,
      options: {
        cwd,
        permissionMode: "bypassPermissions",
        allowDangerouslySkipPermissions: true,
        mcpServers: {
          "unity-error-solver": {
            command: "node",
            args: [serverPath],
          },
        },
      },
    })) {
      if (msg.type === "system") {
        request.onStatus?.("⏳ Claude Code 작업 중...");
      }

      // assistant message: show tool use status
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
        if (
          event?.type === "content_block_start" &&
          event.content_block?.type === "tool_use"
        ) {
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
  context?: string,
  language?: string,
  projectPath?: string
): string {
  let prompt =
    "You are a Unity development expert assistant embedded in a Unity Editor plugin. " +
    "Your primary job is to diagnose and fix Unity errors that prevent the project from compiling or running. " +
    "You can read, create, modify, and delete files in the Unity project. " +
    "You have expertise in C# scripting, Unity APIs, Unity Editor, build systems, and all Unity workflows. " +
    "Do NOT ask the user for file paths or project paths — the working directory is already set to the Unity project root. " +
    "When fixing errors:\n" +
    "1. First read the relevant source file(s) mentioned in the error\n" +
    "2. Understand the root cause\n" +
    "3. Apply the fix by writing the corrected file\n" +
    "4. Explain what you changed and why\n" +
    "Answer clearly and concisely.\n";

  if (projectPath) {
    prompt += `Unity project path: ${projectPath}\n`;
  }

  if (language) {
    prompt += `IMPORTANT: You MUST respond in ${language}.\n`;
  } else {
    prompt += "Use the user's language when possible.\n";
  }

  prompt += "\n";

  if (context) {
    prompt += `Context:\n${context}\n\n`;
  }

  prompt += `User Question:\n${userPrompt}`;

  return prompt;
}
