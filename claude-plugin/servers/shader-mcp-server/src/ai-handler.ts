import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";
import { existsSync, writeFileSync, unlinkSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

interface AIRequest {
  prompt: string;
  context?: string;
  language?: string;
  projectPath?: string;
  geminiApiKey?: string;
  geminiModel?: string;
  referenceImage?: string;
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
    request.projectPath,
    !!request.referenceImage
  );

  // Use Unity project path as cwd if available, fallback to tmpdir
  const cwd =
    request.projectPath && existsSync(request.projectPath)
      ? request.projectPath
      : tmpdir();

  // Set Gemini config in process.env so child processes inherit them
  if (request.geminiApiKey) process.env.GEMINI_API_KEY = request.geminiApiKey;
  if (request.geminiModel) process.env.GEMINI_MODEL = request.geminiModel;

  // Write reference image to temp file (too large for env var)
  let refImageTempPath: string | undefined;
  if (request.referenceImage) {
    refImageTempPath = join(tmpdir(), `unity-agent-ref-${Date.now()}.b64`);
    writeFileSync(refImageTempPath, request.referenceImage, "utf-8");
    process.env.GEMINI_REFERENCE_IMAGE_PATH = refImageTempPath;
  } else {
    delete process.env.GEMINI_REFERENCE_IMAGE_PATH;
  }
  delete process.env.GEMINI_REFERENCE_IMAGE;

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
          "unity-agent-tools": {
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
  } finally {
    // Clean up temp reference image file
    if (refImageTempPath) {
      try { unlinkSync(refImageTempPath); } catch {}
    }
  }
}

function buildFullPrompt(
  userPrompt: string,
  context?: string,
  language?: string,
  projectPath?: string,
  hasReferenceImage?: boolean
): string {
  let prompt =
    "You are a Unity development expert assistant embedded in a Unity Editor plugin. " +
    "You can read, create, modify, and delete files in the Unity project. " +
    "You have expertise in shaders (HLSL/ShaderLab), C# scripts, materials, textures, and all Unity workflows. " +
    "You can also diagnose and fix Unity errors that prevent the project from compiling or running. " +
    "You can generate images using the generate_image tool (powered by Google Nano Banana / Gemini Image). " +
    "When the user asks to create a texture, sprite, icon, or any visual asset, use the generate_image tool with a detailed prompt. " +
    "The generated image will appear in the Unity Editor where the user can save it to their project. " +
    "Do NOT ask the user for file paths or project paths — the working directory is already set to the Unity project root. " +
    "When fixing errors: read the relevant source files, understand the root cause, apply the fix, and explain what you changed. " +
    "Answer clearly and concisely. When the user asks you to modify or create files, do it directly.\n";

  if (hasReferenceImage) {
    prompt +=
      "IMPORTANT: The user has attached a reference image in the UI. " +
      "When generating images, set useReferenceImage to true so the reference image is sent to Gemini. " +
      "The reference image can be used for style transfer, color changes, edits, or as inspiration.\n";
  }

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
