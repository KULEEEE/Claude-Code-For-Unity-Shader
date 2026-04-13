import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { UnityBridge } from "./unity-bridge.js";
import { ShaderLspClient } from "./lsp-client.js";
import { handleAIQuery, handleImageEnhance } from "./ai-handler.js";
import { generateImage } from "./gemini-handler.js";

// Shader Tools
import { registerShaderCompileTool } from "./tools/shader-compile.js";
import { registerShaderAnalyzeTools } from "./tools/shader-analyze.js";
import { registerShaderVariantsTools } from "./tools/shader-variants.js";
import { registerShaderPropertiesTools } from "./tools/shader-properties.js";
import { registerMaterialInfoTools } from "./tools/material-info.js";

// LSP Tools
import { registerLspHoverTool } from "./tools/lsp-hover.js";
import { registerLspCompletionTool } from "./tools/lsp-completion.js";
import { registerLspSignatureTool } from "./tools/lsp-signature.js";
import { registerLspDiagnosticsTool } from "./tools/lsp-diagnostics.js";

// Error Solver Tools
import { registerGetUnityErrorsTool } from "./tools/get-unity-errors.js";
import { registerReadProjectFileTool } from "./tools/read-project-file.js";
import { registerWriteProjectFileTool } from "./tools/write-project-file.js";
import { registerListProjectFilesTool } from "./tools/list-project-files.js";

// Image Generation Tools
import { registerGenerateImageTool, geminiConfig } from "./tools/generate-image.js";

// Frame Debugger Tools
import { registerFrameDebugCaptureTool } from "./tools/framedebug-capture.js";
import { registerFrameDebugEventTool } from "./tools/framedebug-event.js";
import { registerFrameDebugRtTool } from "./tools/framedebug-rt.js";

// Resources
import { registerPipelineInfoResource } from "./resources/pipeline-info.js";
import { registerShaderIncludesResource } from "./resources/shader-includes.js";
import { registerShaderKeywordsResource } from "./resources/shader-keywords.js";
import { registerEditorPlatformResource } from "./resources/editor-platform.js";

async function main(): Promise<void> {
  const server = new McpServer({
    name: "unity-agent-tools",
    version: "0.9.0",
  });

  const bridge = new UnityBridge("ws://localhost:8090");
  const lspClient = new ShaderLspClient();

  // ── Shader Tools ──
  registerShaderCompileTool(server, bridge);
  registerShaderAnalyzeTools(server, bridge);
  registerShaderVariantsTools(server, bridge);
  registerShaderPropertiesTools(server, bridge);
  registerMaterialInfoTools(server, bridge);

  // ── LSP Tools ──
  registerLspHoverTool(server, lspClient);
  registerLspCompletionTool(server, lspClient);
  registerLspSignatureTool(server, lspClient);
  registerLspDiagnosticsTool(server, lspClient);

  // ── Error Solver Tools ──
  registerGetUnityErrorsTool(server, bridge);
  registerReadProjectFileTool(server, bridge);
  registerWriteProjectFileTool(server, bridge);
  registerListProjectFilesTool(server, bridge);

  // ── Image Generation Tools (Nano Banana) ──
  registerGenerateImageTool(server, bridge);

  // ── Frame Debugger Tools ──
  registerFrameDebugCaptureTool(server, bridge);
  registerFrameDebugEventTool(server, bridge);
  registerFrameDebugRtTool(server, bridge);

  // ── Resources ──
  registerPipelineInfoResource(server, bridge);
  registerShaderIncludesResource(server, bridge);
  registerShaderKeywordsResource(server, bridge);
  registerEditorPlatformResource(server, bridge);

  // Register AI query handler (Unity → MCP → Claude CLI → MCP → Unity)
  bridge.onMessage(async (msg) => {
    if (msg.method !== "ai/query") return;

    const id = msg.id as string;
    const params = msg.params as {
      prompt?: string;
      context?: string;
      shaderContext?: string;
      language?: string;
      projectPath?: string;
      geminiApiKey?: string;
      geminiModel?: string;
      referenceImage?: string;
      referenceImagePath?: string;
      imageBackend?: string;
      comfyuiUrl?: string;
    } | undefined;

    if (!id || !params?.prompt) {
      console.error("[UnityAgent] Invalid AI query: missing id or prompt");
      return;
    }

    // Update Gemini config from Unity settings
    // Load reference image from temp file if path provided
    let refImageData: string | undefined;
    if (params.referenceImagePath) {
      try {
        const { readFileSync } = await import("fs");
        refImageData = readFileSync(params.referenceImagePath, "utf-8");
        console.error(`[NanoBanana] Reference image loaded from ${params.referenceImagePath}`);
      } catch (e) {
        console.error(`[NanoBanana] Failed to read reference image: ${e}`);
      }
    } else if (params.referenceImage) {
      refImageData = params.referenceImage;
    }

    if (params.geminiApiKey) {
      geminiConfig.apiKey = params.geminiApiKey;
      geminiConfig.model = params.geminiModel || geminiConfig.model;
      geminiConfig.referenceImage = refImageData || undefined;
      console.error(`[NanoBanana] Config updated: model=${geminiConfig.model}, hasRef=${!!refImageData}`);
    }

    console.error(
      `[UnityAgent] AI query received (id=${id}): ${(params.prompt as string).substring(0, 80)}...`
    );

    try {
      const result = await handleAIQuery({
        prompt: params.prompt,
        context: params.context ?? params.shaderContext,
        language: params.language,
        projectPath: params.projectPath,
        geminiApiKey: params.geminiApiKey,
        geminiModel: params.geminiModel,
        referenceImage: refImageData,
        imageBackend: params.imageBackend,
        comfyuiUrl: params.comfyuiUrl,
        onChunk: (chunk: string) => {
          bridge.sendRaw({ method: "ai/chunk", id, chunk });
        },
        onStatus: (status: string) => {
          bridge.sendRaw({ method: "ai/status", id, status });
        },
      });

      if (result.success) {
        bridge.sendRaw({ method: "ai/response", id, result: result.response });
      } else {
        bridge.sendRaw({ method: "ai/response", id, error: result.error });
      }
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      bridge.sendRaw({ method: "ai/response", id, error: `AI handler error: ${errMsg}` });
    }
  });

  // Register Image Enhance handler (Image Gen mode)
  bridge.onMessage(async (msg) => {
    if (msg.method !== "image/enhance") return;

    const id = msg.id as string;
    const params = msg.params as {
      prompt?: string;
      language?: string;
      projectPath?: string;
      geminiApiKey?: string;
      geminiModel?: string;
      referenceImagePath?: string;
      imageBackend?: string;
      comfyuiUrl?: string;
    } | undefined;

    if (!id || !params?.prompt) {
      console.error("[UnityAgent] Invalid image/enhance: missing id or prompt");
      return;
    }

    // Load reference image from temp file
    let refImageData: string | undefined;
    if (params.referenceImagePath) {
      try {
        const { readFileSync } = await import("fs");
        refImageData = readFileSync(params.referenceImagePath, "utf-8");
      } catch (e) {
        console.error("[ImageGen] Failed to read reference image: " + e);
      }
    }

    // Update Gemini config
    if (params.geminiApiKey) {
      geminiConfig.apiKey = params.geminiApiKey;
      geminiConfig.model = params.geminiModel || geminiConfig.model;
    }

    const backend = params.imageBackend || "gemini";
    console.error(`[ImageGen] Image enhance request (${backend}): "${params.prompt.substring(0, 60)}..."`);

    try {
      // Step 1: Claude enhances the prompt (with multimodal ref image)
      bridge.sendRaw({ method: "ai/status", id, status: "🎨 Claude is enhancing your prompt..." });

      const enhanceResult = await handleImageEnhance({
        prompt: params.prompt,
        language: params.language,
        referenceImage: refImageData,
        onStatus: (status: string) => {
          bridge.sendRaw({ method: "ai/status", id, status });
        },
      });

      if (!enhanceResult.success || !enhanceResult.enhancedPrompt) {
        bridge.sendRaw({ method: "ai/response", id, error: `Prompt enhance failed: ${enhanceResult.error}` });
        return;
      }

      console.error(`[ImageGen] Enhanced prompt: "${enhanceResult.enhancedPrompt.substring(0, 80)}..."`);

      // Step 2: Generate image with selected backend
      let imageResult: { success: boolean; imageBase64?: string; description?: string; error?: string };

      if (backend === "comfyui") {
        bridge.sendRaw({ method: "ai/status", id, status: "🖼️ Generating image with ComfyUI..." });
        const { generateImageComfyUI } = await import("./comfyui-handler.js");
        imageResult = await generateImageComfyUI({
          serverUrl: params.comfyuiUrl || "http://127.0.0.1:8188",
          prompt: enhanceResult.enhancedPrompt,
          referenceImage: refImageData,
          onStatus: (status: string) => {
            bridge.sendRaw({ method: "ai/status", id, status });
          },
        });
      } else {
        bridge.sendRaw({ method: "ai/status", id, status: "🖼️ Generating image with Gemini..." });
        imageResult = await generateImage({
          apiKey: geminiConfig.apiKey,
          model: geminiConfig.model,
          prompt: refImageData
            ? `Edit the provided image according to these instructions: ${enhanceResult.enhancedPrompt}`
            : enhanceResult.enhancedPrompt,
          referenceImage: refImageData,
        });
      }

      if (imageResult.success && imageResult.imageBase64) {
        bridge.sendRaw({
          method: "image/generated",
          imageData: imageResult.imageBase64,
          description: imageResult.description || `Generated: ${enhanceResult.enhancedPrompt.substring(0, 60)}`,
        });

        let responseText = "";
        if (enhanceResult.explanation) {
          responseText = enhanceResult.explanation;
        }
        responseText += `\n\nEnhanced prompt: "${enhanceResult.enhancedPrompt}"`;
        bridge.sendRaw({ method: "ai/response", id, result: responseText });
      } else {
        bridge.sendRaw({ method: "ai/response", id, error: `Image generation failed: ${imageResult.error}` });
      }
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      bridge.sendRaw({ method: "ai/response", id, error: `Image enhance error: ${errMsg}` });
    }
  });

  // Connect to Unity
  bridge.connect().catch(() => {
    console.error("[UnityAgent] Initial connection to Unity failed. Will retry automatically.");
  });

  // Start MCP server on stdio
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[UnityAgent] MCP server started on stdio");

  // Cleanup on exit
  const cleanup = async () => {
    await lspClient.shutdown();
    bridge.disconnect();
    process.exit(0);
  };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main().catch((err) => {
  console.error(`[UnityAgent] Fatal error: ${err}`);
  process.exit(1);
});
