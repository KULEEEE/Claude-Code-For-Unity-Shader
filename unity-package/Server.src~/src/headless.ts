/**
 * Headless AI runner spawned by Unity per request.
 *
 * Protocol:
 *   Unity writes ONE line of JSON to stdin:
 *     { "id", "method": "ai/query" | "image/enhance", "params": {...} }
 *   Headless writes JSON lines to stdout as events fire:
 *     { "type": "status" | "chunk" | "image" | "result" | "error", ... }
 *   Headless exits when the request is complete.
 */
import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  unlinkSync,
  writeFileSync,
} from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

interface QueryParams {
  prompt?: string;
  context?: string;
  language?: string;
  projectPath?: string;
  geminiApiKey?: string;
  geminiModel?: string;
  referenceImagePath?: string;
  imageBackend?: string;
  comfyuiUrl?: string;
}

interface IncomingMessage {
  id?: string;
  method?: string;
  params?: QueryParams;
}

function emit(obj: Record<string, unknown>): void {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function buildFullPrompt(params: QueryParams, hasReferenceImage: boolean): string {
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
      "When generating images, set useReferenceImage to true so the reference image is sent to the image backend.\n";
  }

  if (params.projectPath) prompt += `Unity project path: ${params.projectPath}\n`;
  if (params.language) prompt += `IMPORTANT: You MUST respond in ${params.language}.\n`;
  else prompt += "Use the user's language when possible.\n";

  prompt += "\n";
  if (params.context) prompt += `Context:\n${params.context}\n\n`;
  prompt += `User Question:\n${params.prompt ?? ""}`;
  return prompt;
}

function applyEnv(params: QueryParams, imageOutDir: string): void {
  if (params.geminiApiKey) process.env.GEMINI_API_KEY = params.geminiApiKey;
  if (params.geminiModel) process.env.GEMINI_MODEL = params.geminiModel;
  if (params.imageBackend) process.env.IMAGE_BACKEND = params.imageBackend;
  if (params.comfyuiUrl) process.env.COMFYUI_URL = params.comfyuiUrl;
  if (params.referenceImagePath) {
    process.env.GEMINI_REFERENCE_IMAGE_PATH = params.referenceImagePath;
  } else {
    delete process.env.GEMINI_REFERENCE_IMAGE_PATH;
  }
  process.env.UNITY_IMAGE_OUT_DIR = imageOutDir;
}

function drainImageEvents(dir: string, seen: Set<string>): void {
  if (!existsSync(dir)) return;
  let entries: string[];
  try { entries = readdirSync(dir); } catch { return; }
  for (const name of entries) {
    if (seen.has(name)) continue;
    seen.add(name);
    const full = join(dir, name);
    try {
      const raw = readFileSync(full, "utf-8");
      const parsed = JSON.parse(raw) as { imageData?: string; description?: string };
      if (parsed.imageData) {
        emit({ type: "image", data: parsed.imageData, description: parsed.description ?? "" });
      }
      try { unlinkSync(full); } catch { /* ignore */ }
    } catch (err) {
      emit({ type: "status", data: `[headless] Failed to read image event: ${err}` });
    }
  }
}

async function readStdinMessage(): Promise<IncomingMessage> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    process.stdin.on("data", (c) => chunks.push(c));
    process.stdin.on("end", () => {
      try {
        const text = Buffer.concat(chunks).toString("utf-8").trim();
        if (!text) throw new Error("Empty input");
        resolve(JSON.parse(text) as IncomingMessage);
      } catch (e) {
        reject(e);
      }
    });
    process.stdin.on("error", reject);
  });
}

async function runAIQuery(params: QueryParams, imageOutDir: string): Promise<void> {
  const seen = new Set<string>();
  const cwd =
    params.projectPath && existsSync(params.projectPath) ? params.projectPath : tmpdir();
  const hasReference = !!params.referenceImagePath;
  const fullPrompt = buildFullPrompt(params, hasReference);

  const mcpToolsPath = join(dirname(fileURLToPath(import.meta.url)), "mcp-tools.mjs");

  emit({ type: "status", data: "⏳ Claude Code 작업 시작..." });

  let resultText = "";

  for await (const msg of query({
    prompt: fullPrompt,
    options: {
      cwd,
      permissionMode: "bypassPermissions",
      allowDangerouslySkipPermissions: true,
      mcpServers: {
        "unity-agent-image": {
          command: "node",
          args: [mcpToolsPath],
        },
      },
    },
  })) {
    drainImageEvents(imageOutDir, seen);

    if (msg.type === "system") {
      emit({ type: "status", data: "⏳ Claude Code 작업 중..." });
    }

    if (msg.type === "assistant") {
      const content = (msg as any).message?.content;
      if (Array.isArray(content)) {
        for (const block of content) {
          if (block.type === "tool_use") {
            emit({ type: "status", data: `⚙️ ${block.name}` });
          }
          if (block.type === "text" && typeof block.text === "string" && block.text.length > 0) {
            emit({ type: "chunk", data: block.text });
          }
        }
      }
    }

    if (msg.type === "tool_progress") {
      const tp = msg as any;
      const elapsed = Math.round(tp.elapsed_time_seconds ?? 0);
      emit({ type: "status", data: `⚙️ ${tp.tool_name} (${elapsed}s)` });
    }

    if (msg.type === "stream_event") {
      const event = (msg as any).event;
      if (event?.type === "content_block_start" && event.content_block?.type === "tool_use") {
        emit({ type: "status", data: `⚙️ ${event.content_block.name}...` });
      }
    }

    if (msg.type === "result") {
      resultText = (msg as any).result ?? "";
    }
  }

  drainImageEvents(imageOutDir, seen);
  emit({ type: "result", data: resultText });
}

async function runImageEnhance(params: QueryParams, imageOutDir: string): Promise<void> {
  // Step 1: Claude enhances the prompt (multimodal with ref image when available).
  emit({ type: "status", data: "🎨 Claude is enhancing your prompt..." });

  const systemPrompt =
    "You are an expert AI image prompt engineer. Your job is to take a user's brief description " +
    "and transform it into a detailed, optimized prompt for Google's Gemini image generation model (Nano Banana). " +
    "If a reference image is provided, analyze it carefully and incorporate its visual characteristics into your prompt. " +
    "Output ONLY the enhanced prompt text — no explanations, no markdown, no prefixes. " +
    "The prompt should be in English for best results with Gemini, but add a brief explanation in the user's language after '---'. " +
    "Format:\n[enhanced prompt in English]\n---\n[brief explanation in user's language]";

  const languageNote = params.language
    ? `\nThe user speaks ${params.language}. Write the explanation after --- in ${params.language}.`
    : "";

  let refImageData: string | undefined;
  if (params.referenceImagePath && existsSync(params.referenceImagePath)) {
    try { refImageData = readFileSync(params.referenceImagePath, "utf-8"); } catch { /* ignore */ }
  }

  async function* generateMessages() {
    const contentParts: Array<unknown> = [];
    if (refImageData) {
      contentParts.push({
        type: "image",
        source: { type: "base64", media_type: "image/png", data: refImageData },
      });
      emit({ type: "status", data: "🎨 Claude is analyzing your reference image..." });
    }
    contentParts.push({
      type: "text",
      text:
        systemPrompt +
        languageNote +
        `\n\nUser's description: "${params.prompt ?? ""}"` +
        (refImageData
          ? "\n\nA reference image is attached above. Analyze its style, colors, composition, and subject, then incorporate these into the enhanced prompt."
          : ""),
    });
    yield {
      type: "user" as const,
      message: { role: "user" as const, content: contentParts },
      parent_tool_use_id: null,
      session_id: "",
    };
  }

  let enhancedFull = "";
  for await (const msg of query({
    prompt: generateMessages() as AsyncIterable<any>,
    options: { maxTurns: 1 },
  })) {
    if (msg.type === "result") enhancedFull = (msg as any).result ?? "";
  }

  const parts = enhancedFull.split("---");
  const enhancedPrompt = parts[0].trim();
  const explanation = parts.length > 1 ? parts.slice(1).join("---").trim() : "";

  if (!enhancedPrompt) {
    emit({ type: "error", data: "Prompt enhance failed: empty result" });
    return;
  }

  // Step 2: Generate image via backend.
  const backend = params.imageBackend || "gemini";
  emit({
    type: "status",
    data: backend === "comfyui" ? "🖼️ Generating image with ComfyUI..." : "🖼️ Generating image with Gemini...",
  });

  let imageResult: { success: boolean; imageBase64?: string; description?: string; error?: string };
  if (backend === "comfyui") {
    const { generateImageComfyUI } = await import("./comfyui-handler.js");
    imageResult = await generateImageComfyUI({
      serverUrl: params.comfyuiUrl || "http://127.0.0.1:8188",
      prompt: enhancedPrompt,
      referenceImage: refImageData,
      onStatus: (s) => emit({ type: "status", data: s }),
    });
  } else {
    const { generateImage } = await import("./gemini-handler.js");
    imageResult = await generateImage({
      apiKey: params.geminiApiKey || "",
      model: params.geminiModel || "gemini-2.5-flash-image",
      prompt: refImageData
        ? `Edit the provided image according to these instructions: ${enhancedPrompt}`
        : enhancedPrompt,
      referenceImage: refImageData,
    });
  }

  if (imageResult.success && imageResult.imageBase64) {
    emit({
      type: "image",
      data: imageResult.imageBase64,
      description:
        imageResult.description || `Generated: ${enhancedPrompt.substring(0, 60)}`,
    });
    const responseText =
      (explanation ? explanation + "\n\n" : "") +
      `Enhanced prompt: "${enhancedPrompt}"`;
    emit({ type: "result", data: responseText });
  } else {
    emit({ type: "error", data: `Image generation failed: ${imageResult.error}` });
  }
}

async function main(): Promise<void> {
  const msg = await readStdinMessage();
  const method = msg.method ?? "ai/query";
  const params = msg.params ?? {};

  const imageOutDir = join(
    tmpdir(),
    `unity-agent-img-${process.pid}-${Date.now()}`
  );
  mkdirSync(imageOutDir, { recursive: true });

  applyEnv(params, imageOutDir);

  try {
    if (method === "image/enhance") {
      await runImageEnhance(params, imageOutDir);
    } else {
      await runAIQuery(params, imageOutDir);
    }
  } catch (err) {
    const m = err instanceof Error ? err.message : String(err);
    emit({ type: "error", data: m });
  }
}

main().catch((err) => {
  const m = err instanceof Error ? err.message : String(err);
  emit({ type: "error", data: `Fatal: ${m}` });
  process.exit(1);
});
