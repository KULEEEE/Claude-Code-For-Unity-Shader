#!/usr/bin/env node
import { createRequire } from 'module'; const require = createRequire(import.meta.url);
var __defProp = Object.defineProperty;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __esm = (fn, res) => function __init() {
  return fn && (res = (0, fn[__getOwnPropNames(fn)[0]])(fn = 0)), res;
};
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};

// build/comfyui-handler.js
var comfyui_handler_exports = {};
__export(comfyui_handler_exports, {
  generateImageComfyUI: () => generateImageComfyUI
});
import { randomUUID } from "crypto";
async function generateImageComfyUI(request) {
  const { serverUrl, prompt, referenceImage, onStatus } = request;
  const baseUrl = serverUrl.replace(/\/+$/, "");
  try {
    onStatus?.("\u{1F517} Connecting to ComfyUI...");
    try {
      const healthCheck = await fetch(`${baseUrl}/system_stats`, {
        signal: AbortSignal.timeout(5e3)
      });
      if (!healthCheck.ok)
        throw new Error(`HTTP ${healthCheck.status}`);
    } catch {
      return {
        success: false,
        error: `Cannot connect to ComfyUI at ${baseUrl}. Ensure ComfyUI is running.`
      };
    }
    onStatus?.("\u{1F4E6} Finding checkpoint model...");
    const checkpoint = await findCheckpoint(baseUrl);
    if (!checkpoint) {
      return {
        success: false,
        error: "No checkpoint models found in ComfyUI. Please install a model (e.g., SDXL) to the models/checkpoints folder."
      };
    }
    console.error(`[ComfyUI] Using checkpoint: ${checkpoint}`);
    let uploadedImageName;
    if (referenceImage) {
      onStatus?.("\u{1F4E4} Uploading reference image...");
      uploadedImageName = await uploadImage(baseUrl, referenceImage) ?? void 0;
      if (!uploadedImageName) {
        console.error("[ComfyUI] Failed to upload reference image, proceeding with txt2img");
      }
    }
    const clientId = randomUUID();
    const workflow = uploadedImageName ? buildImg2ImgWorkflow(checkpoint, prompt, uploadedImageName) : buildTxt2ImgWorkflow(checkpoint, prompt);
    onStatus?.("\u2699\uFE0F Generating with ComfyUI...");
    const submitResponse = await fetch(`${baseUrl}/prompt`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ prompt: workflow, client_id: clientId })
    });
    if (!submitResponse.ok) {
      const errorText = await submitResponse.text();
      return {
        success: false,
        error: `ComfyUI prompt submission failed: ${errorText.substring(0, 200)}`
      };
    }
    const submitData = await submitResponse.json();
    if (!submitData.prompt_id) {
      return { success: false, error: `ComfyUI error: ${submitData.error || "No prompt_id returned"}` };
    }
    const promptId = submitData.prompt_id;
    console.error(`[ComfyUI] Prompt submitted: ${promptId}`);
    const imageFilename = await pollForResult(baseUrl, promptId, onStatus);
    if (!imageFilename) {
      return { success: false, error: "ComfyUI generation timed out or failed." };
    }
    onStatus?.("\u{1F4E5} Downloading generated image...");
    const imageBase64 = await fetchImage(baseUrl, imageFilename);
    if (!imageBase64) {
      return { success: false, error: "Failed to download generated image from ComfyUI." };
    }
    console.error("[ComfyUI] Image generated successfully.");
    return {
      success: true,
      imageBase64,
      description: `Generated with ComfyUI (${checkpoint})`
    };
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    console.error(`[ComfyUI] Error: ${msg}`);
    return { success: false, error: `ComfyUI error: ${msg}` };
  }
}
async function findCheckpoint(baseUrl) {
  try {
    const response = await fetch(`${baseUrl}/object_info/CheckpointLoaderSimple`);
    if (!response.ok)
      return null;
    const data = await response.json();
    const inputs = data?.CheckpointLoaderSimple?.input?.required?.ckpt_name;
    if (Array.isArray(inputs) && Array.isArray(inputs[0]) && inputs[0].length > 0) {
      return inputs[0][0];
    }
    return null;
  } catch {
    return null;
  }
}
async function uploadImage(baseUrl, base64Data) {
  try {
    const imageBuffer = Buffer.from(base64Data, "base64");
    const filename = `unity_ref_${Date.now()}.png`;
    const formData = new FormData();
    formData.append("image", new Blob([imageBuffer], { type: "image/png" }), filename);
    formData.append("overwrite", "true");
    const response = await fetch(`${baseUrl}/upload/image`, {
      method: "POST",
      body: formData
    });
    if (!response.ok)
      return null;
    const data = await response.json();
    return data.name || filename;
  } catch {
    return null;
  }
}
function buildTxt2ImgWorkflow(checkpoint, prompt) {
  return {
    "4": {
      class_type: "CheckpointLoaderSimple",
      inputs: { ckpt_name: checkpoint }
    },
    "6": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: prompt,
        clip: ["4", 1]
      }
    },
    "7": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: "blurry, low quality, distorted, deformed, ugly, watermark",
        clip: ["4", 1]
      }
    },
    "5": {
      class_type: "EmptyLatentImage",
      inputs: { width: 1024, height: 1024, batch_size: 1 }
    },
    "3": {
      class_type: "KSampler",
      inputs: {
        seed: Math.floor(Math.random() * 2 ** 32),
        steps: 20,
        cfg: 7,
        sampler_name: "euler",
        scheduler: "normal",
        denoise: 1,
        model: ["4", 0],
        positive: ["6", 0],
        negative: ["7", 0],
        latent_image: ["5", 0]
      }
    },
    "8": {
      class_type: "VAEDecode",
      inputs: {
        samples: ["3", 0],
        vae: ["4", 2]
      }
    },
    "9": {
      class_type: "SaveImage",
      inputs: {
        filename_prefix: "UnityAgent",
        images: ["8", 0]
      }
    }
  };
}
function buildImg2ImgWorkflow(checkpoint, prompt, inputImageName) {
  return {
    "4": {
      class_type: "CheckpointLoaderSimple",
      inputs: { ckpt_name: checkpoint }
    },
    "6": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: prompt,
        clip: ["4", 1]
      }
    },
    "7": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: "blurry, low quality, distorted, deformed, ugly, watermark",
        clip: ["4", 1]
      }
    },
    "10": {
      class_type: "LoadImage",
      inputs: { image: inputImageName }
    },
    "11": {
      class_type: "VAEEncode",
      inputs: {
        pixels: ["10", 0],
        vae: ["4", 2]
      }
    },
    "3": {
      class_type: "KSampler",
      inputs: {
        seed: Math.floor(Math.random() * 2 ** 32),
        steps: 20,
        cfg: 7,
        sampler_name: "euler",
        scheduler: "normal",
        denoise: 0.7,
        model: ["4", 0],
        positive: ["6", 0],
        negative: ["7", 0],
        latent_image: ["11", 0]
      }
    },
    "8": {
      class_type: "VAEDecode",
      inputs: {
        samples: ["3", 0],
        vae: ["4", 2]
      }
    },
    "9": {
      class_type: "SaveImage",
      inputs: {
        filename_prefix: "UnityAgent",
        images: ["8", 0]
      }
    }
  };
}
async function pollForResult(baseUrl, promptId, onStatus) {
  const maxWait = 12e4;
  const pollInterval = 2e3;
  const startTime = Date.now();
  while (Date.now() - startTime < maxWait) {
    await new Promise((r) => setTimeout(r, pollInterval));
    const elapsed = Math.round((Date.now() - startTime) / 1e3);
    onStatus?.(`\u2699\uFE0F ComfyUI generating... (${elapsed}s)`);
    try {
      const response = await fetch(`${baseUrl}/history/${promptId}`);
      if (!response.ok)
        continue;
      const data = await response.json();
      const entry = data[promptId];
      if (!entry)
        continue;
      const outputs = entry.outputs;
      if (outputs) {
        for (const nodeId of Object.keys(outputs)) {
          const nodeOutput = outputs[nodeId];
          if (nodeOutput.images && nodeOutput.images.length > 0) {
            const img = nodeOutput.images[0];
            return `${img.filename}|${img.subfolder || ""}|${img.type || "output"}`;
          }
        }
      }
    } catch {
    }
  }
  return null;
}
async function fetchImage(baseUrl, imageInfo) {
  try {
    const [filename, subfolder, type] = imageInfo.split("|");
    const params = new URLSearchParams({
      filename,
      subfolder: subfolder || "",
      type: type || "output"
    });
    const response = await fetch(`${baseUrl}/view?${params}`);
    if (!response.ok)
      return null;
    const buffer = Buffer.from(await response.arrayBuffer());
    return buffer.toString("base64");
  } catch {
    return null;
  }
}
var init_comfyui_handler = __esm({
  "build/comfyui-handler.js"() {
    "use strict";
  }
});

// build/gemini-handler.js
var gemini_handler_exports = {};
__export(gemini_handler_exports, {
  generateImage: () => generateImage
});
async function generateImage(request) {
  const { apiKey, model, prompt, referenceImage } = request;
  if (!apiKey) {
    return {
      success: false,
      error: "Gemini API key not configured. Please set it in AI Chat > Settings."
    };
  }
  const url = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${apiKey}`;
  const parts = [];
  if (referenceImage) {
    parts.push({
      inlineData: {
        mimeType: "image/png",
        data: referenceImage
      }
    });
  }
  const finalPrompt = referenceImage ? `Edit the provided image according to these instructions: ${prompt}` : prompt;
  parts.push({
    text: finalPrompt
  });
  const body = {
    contents: [
      {
        parts
      }
    ],
    generationConfig: {
      responseModalities: ["TEXT", "IMAGE"]
    }
  };
  try {
    console.error(`[NanoBanana] Generating image with ${model}: "${prompt.substring(0, 60)}..."`);
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    if (!response.ok) {
      const errorText = await response.text();
      let errorMsg = `Gemini API error (${response.status})`;
      try {
        const errorJson = JSON.parse(errorText);
        errorMsg = errorJson.error?.message || errorMsg;
      } catch {
        errorMsg += `: ${errorText.substring(0, 200)}`;
      }
      return { success: false, error: errorMsg };
    }
    const data = await response.json();
    let imageBase64;
    let description;
    const candidates = data.candidates;
    if (candidates && candidates.length > 0) {
      const parts2 = candidates[0].content?.parts;
      if (parts2) {
        for (const part of parts2) {
          if (part.inlineData?.data) {
            imageBase64 = part.inlineData.data;
          }
          if (part.text) {
            description = part.text;
          }
        }
      }
    }
    if (!imageBase64) {
      return {
        success: false,
        error: "Gemini returned no image data. The model may not support image generation, or the prompt was filtered.",
        ...description ? { description } : {}
      };
    }
    console.error("[NanoBanana] Image generated successfully.");
    return {
      success: true,
      imageBase64,
      description: description || "Generated image"
    };
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    console.error(`[NanoBanana] Error: ${msg}`);
    return { success: false, error: `Gemini request failed: ${msg}` };
  }
}
var init_gemini_handler = __esm({
  "build/gemini-handler.js"() {
    "use strict";
  }
});

// build/headless.js
import { query } from "@anthropic-ai/claude-agent-sdk";
import { tmpdir } from "os";
import { existsSync, mkdirSync, readdirSync, readFileSync, unlinkSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";
function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}
function buildFullPrompt(params, hasReferenceImage) {
  let prompt = "You are a Unity development expert assistant embedded in a Unity Editor plugin. You can read, create, modify, and delete files in the Unity project. You have expertise in shaders (HLSL/ShaderLab), C# scripts, materials, textures, and all Unity workflows. You can also diagnose and fix Unity errors that prevent the project from compiling or running. You can generate images using the generate_image tool (powered by Google Nano Banana / Gemini Image). When the user asks to create a texture, sprite, icon, or any visual asset, use the generate_image tool with a detailed prompt. The generated image will appear in the Unity Editor where the user can save it to their project. Do NOT ask the user for file paths or project paths \u2014 the working directory is already set to the Unity project root. When fixing errors: read the relevant source files, understand the root cause, apply the fix, and explain what you changed. Answer clearly and concisely. When the user asks you to modify or create files, do it directly.\n";
  if (hasReferenceImage) {
    prompt += "IMPORTANT: The user has attached a reference image in the UI. When generating images, set useReferenceImage to true so the reference image is sent to the image backend.\n";
  }
  if (params.projectPath)
    prompt += `Unity project path: ${params.projectPath}
`;
  if (params.language)
    prompt += `IMPORTANT: You MUST respond in ${params.language}.
`;
  else
    prompt += "Use the user's language when possible.\n";
  prompt += "\n";
  if (params.context)
    prompt += `Context:
${params.context}

`;
  prompt += `User Question:
${params.prompt ?? ""}`;
  return prompt;
}
function applyEnv(params, imageOutDir) {
  if (params.geminiApiKey)
    process.env.GEMINI_API_KEY = params.geminiApiKey;
  if (params.geminiModel)
    process.env.GEMINI_MODEL = params.geminiModel;
  if (params.imageBackend)
    process.env.IMAGE_BACKEND = params.imageBackend;
  if (params.comfyuiUrl)
    process.env.COMFYUI_URL = params.comfyuiUrl;
  if (params.referenceImagePath) {
    process.env.GEMINI_REFERENCE_IMAGE_PATH = params.referenceImagePath;
  } else {
    delete process.env.GEMINI_REFERENCE_IMAGE_PATH;
  }
  process.env.UNITY_IMAGE_OUT_DIR = imageOutDir;
}
function drainImageEvents(dir, seen) {
  if (!existsSync(dir))
    return;
  let entries;
  try {
    entries = readdirSync(dir);
  } catch {
    return;
  }
  for (const name of entries) {
    if (seen.has(name))
      continue;
    seen.add(name);
    const full = join(dir, name);
    try {
      const raw = readFileSync(full, "utf-8");
      const parsed = JSON.parse(raw);
      if (parsed.imageData) {
        emit({ type: "image", data: parsed.imageData, description: parsed.description ?? "" });
      }
      try {
        unlinkSync(full);
      } catch {
      }
    } catch (err) {
      emit({ type: "status", data: `[headless] Failed to read image event: ${err}` });
    }
  }
}
async function readStdinMessage() {
  return new Promise((resolve, reject) => {
    const chunks = [];
    process.stdin.on("data", (c) => chunks.push(c));
    process.stdin.on("end", () => {
      try {
        const text = Buffer.concat(chunks).toString("utf-8").trim();
        if (!text)
          throw new Error("Empty input");
        resolve(JSON.parse(text));
      } catch (e) {
        reject(e);
      }
    });
    process.stdin.on("error", reject);
  });
}
async function runAIQuery(params, imageOutDir) {
  const seen = /* @__PURE__ */ new Set();
  const cwd = params.projectPath && existsSync(params.projectPath) ? params.projectPath : tmpdir();
  const hasReference = !!params.referenceImagePath;
  const fullPrompt = buildFullPrompt(params, hasReference);
  const mcpToolsPath = join(dirname(fileURLToPath(import.meta.url)), "mcp-tools.mjs");
  emit({ type: "status", data: "\u23F3 Claude Code \uC791\uC5C5 \uC2DC\uC791..." });
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
          args: [mcpToolsPath]
        }
      }
    }
  })) {
    drainImageEvents(imageOutDir, seen);
    if (msg.type === "system") {
      emit({ type: "status", data: "\u23F3 Claude Code \uC791\uC5C5 \uC911..." });
    }
    if (msg.type === "assistant") {
      const content = msg.message?.content;
      if (Array.isArray(content)) {
        for (const block of content) {
          if (block.type === "tool_use") {
            emit({ type: "status", data: `\u2699\uFE0F ${block.name}` });
          }
          if (block.type === "text" && typeof block.text === "string" && block.text.length > 0) {
            emit({ type: "chunk", data: block.text });
          }
        }
      }
    }
    if (msg.type === "tool_progress") {
      const tp = msg;
      const elapsed = Math.round(tp.elapsed_time_seconds ?? 0);
      emit({ type: "status", data: `\u2699\uFE0F ${tp.tool_name} (${elapsed}s)` });
    }
    if (msg.type === "stream_event") {
      const event = msg.event;
      if (event?.type === "content_block_start" && event.content_block?.type === "tool_use") {
        emit({ type: "status", data: `\u2699\uFE0F ${event.content_block.name}...` });
      }
    }
    if (msg.type === "result") {
      resultText = msg.result ?? "";
    }
  }
  drainImageEvents(imageOutDir, seen);
  emit({ type: "result", data: resultText });
}
async function runImageEnhance(params, imageOutDir) {
  emit({ type: "status", data: "\u{1F3A8} Claude is enhancing your prompt..." });
  const systemPrompt = "You are an expert AI image prompt engineer. Your job is to take a user's brief description and transform it into a detailed, optimized prompt for Google's Gemini image generation model (Nano Banana). If a reference image is provided, analyze it carefully and incorporate its visual characteristics into your prompt. Output ONLY the enhanced prompt text \u2014 no explanations, no markdown, no prefixes. The prompt should be in English for best results with Gemini, but add a brief explanation in the user's language after '---'. Format:\n[enhanced prompt in English]\n---\n[brief explanation in user's language]";
  const languageNote = params.language ? `
The user speaks ${params.language}. Write the explanation after --- in ${params.language}.` : "";
  let refImageData;
  if (params.referenceImagePath && existsSync(params.referenceImagePath)) {
    try {
      refImageData = readFileSync(params.referenceImagePath, "utf-8");
    } catch {
    }
  }
  async function* generateMessages() {
    const contentParts = [];
    if (refImageData) {
      contentParts.push({
        type: "image",
        source: { type: "base64", media_type: "image/png", data: refImageData }
      });
      emit({ type: "status", data: "\u{1F3A8} Claude is analyzing your reference image..." });
    }
    contentParts.push({
      type: "text",
      text: systemPrompt + languageNote + `

User's description: "${params.prompt ?? ""}"` + (refImageData ? "\n\nA reference image is attached above. Analyze its style, colors, composition, and subject, then incorporate these into the enhanced prompt." : "")
    });
    yield {
      type: "user",
      message: { role: "user", content: contentParts },
      parent_tool_use_id: null,
      session_id: ""
    };
  }
  let enhancedFull = "";
  for await (const msg of query({
    prompt: generateMessages(),
    options: { maxTurns: 1 }
  })) {
    if (msg.type === "result")
      enhancedFull = msg.result ?? "";
  }
  const parts = enhancedFull.split("---");
  const enhancedPrompt = parts[0].trim();
  const explanation = parts.length > 1 ? parts.slice(1).join("---").trim() : "";
  if (!enhancedPrompt) {
    emit({ type: "error", data: "Prompt enhance failed: empty result" });
    return;
  }
  const backend = params.imageBackend || "gemini";
  emit({
    type: "status",
    data: backend === "comfyui" ? "\u{1F5BC}\uFE0F Generating image with ComfyUI..." : "\u{1F5BC}\uFE0F Generating image with Gemini..."
  });
  let imageResult;
  if (backend === "comfyui") {
    const { generateImageComfyUI: generateImageComfyUI2 } = await Promise.resolve().then(() => (init_comfyui_handler(), comfyui_handler_exports));
    imageResult = await generateImageComfyUI2({
      serverUrl: params.comfyuiUrl || "http://127.0.0.1:8188",
      prompt: enhancedPrompt,
      referenceImage: refImageData,
      onStatus: (s) => emit({ type: "status", data: s })
    });
  } else {
    const { generateImage: generateImage2 } = await Promise.resolve().then(() => (init_gemini_handler(), gemini_handler_exports));
    imageResult = await generateImage2({
      apiKey: params.geminiApiKey || "",
      model: params.geminiModel || "gemini-2.5-flash-image",
      prompt: refImageData ? `Edit the provided image according to these instructions: ${enhancedPrompt}` : enhancedPrompt,
      referenceImage: refImageData
    });
  }
  if (imageResult.success && imageResult.imageBase64) {
    emit({
      type: "image",
      data: imageResult.imageBase64,
      description: imageResult.description || `Generated: ${enhancedPrompt.substring(0, 60)}`
    });
    const responseText = (explanation ? explanation + "\n\n" : "") + `Enhanced prompt: "${enhancedPrompt}"`;
    emit({ type: "result", data: responseText });
  } else {
    emit({ type: "error", data: `Image generation failed: ${imageResult.error}` });
  }
}
async function main() {
  const msg = await readStdinMessage();
  const method = msg.method ?? "ai/query";
  const params = msg.params ?? {};
  const imageOutDir = join(tmpdir(), `unity-agent-img-${process.pid}-${Date.now()}`);
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
