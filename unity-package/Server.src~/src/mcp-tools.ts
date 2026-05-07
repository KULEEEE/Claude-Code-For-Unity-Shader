/**
 * Internal MCP stdio server launched as a child of headless.ts via claude-agent-sdk's
 * mcpServers config. Exposes ONLY the generate_image tool — Unity's shader/framedebug/
 * error-solver tools are not needed here (AI chat already inlines that context in the
 * prompt from C# side).
 *
 * On successful image generation, writes the result as JSON to UNITY_IMAGE_OUT_DIR so
 * the parent headless process can forward it to Unity via stdout.
 */
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { existsSync, readFileSync, writeFileSync, mkdirSync } from "fs";
import { join } from "path";
import { generateImage } from "./gemini-handler.js";
import { generateImageComfyUI } from "./comfyui-handler.js";

function resolveReferenceImage(): string | undefined {
  const path = process.env.GEMINI_REFERENCE_IMAGE_PATH;
  if (path && existsSync(path)) {
    try { return readFileSync(path, "utf-8"); } catch { /* ignore */ }
  }
  return undefined;
}

function writeImageEvent(imageBase64: string, description: string): void {
  const dir = process.env.UNITY_IMAGE_OUT_DIR;
  if (!dir) return;
  try {
    mkdirSync(dir, { recursive: true });
    const file = join(dir, `img-${Date.now()}-${Math.floor(Math.random() * 1e6)}.json`);
    writeFileSync(file, JSON.stringify({ imageData: imageBase64, description }), "utf-8");
  } catch (err) {
    console.error(`[mcp-tools] Failed to write image event: ${err}`);
  }
}

async function main(): Promise<void> {
  const server = new McpServer({ name: "unity-agent-image", version: "0.12.0" });

  server.tool(
    "generate_image",
    "Generate an image using the configured backend (Nano Banana/Gemini or ComfyUI). " +
      "Use this when the user asks to create, generate, or make an image, texture, sprite, icon, or visual asset. " +
      "The generated image will be displayed in the Unity Editor's AI Chat window.",
    {
      prompt: z
        .string()
        .describe(
          "Detailed image generation prompt. Be specific about style, colors, composition, and content. " +
            "For game textures, include terms like 'seamless', 'tileable', 'PBR', etc."
        ),
      useReferenceImage: z
        .boolean()
        .optional()
        .describe("Whether to include the user's reference image (if one is set in the UI)."),
    },
    async ({ prompt, useReferenceImage }) => {
      const backend = process.env.IMAGE_BACKEND || "gemini";
      const shouldUseRef = useReferenceImage !== false;
      const refImage = shouldUseRef ? resolveReferenceImage() : undefined;

      const geminiApiKey = process.env.GEMINI_API_KEY || "";
      const geminiModel = process.env.GEMINI_MODEL || "gemini-2.5-flash-image";
      const comfyuiUrl = process.env.COMFYUI_URL || "http://127.0.0.1:8188";

      if (backend === "gemini" && !geminiApiKey) {
        return {
          content: [
            {
              type: "text" as const,
              text: "Error: Gemini API key is not configured. Set it in AI Chat > Image Gen settings.",
            },
          ],
          isError: true,
        };
      }

      try {
        const result =
          backend === "comfyui"
            ? await generateImageComfyUI({
                serverUrl: comfyuiUrl,
                prompt,
                referenceImage: refImage,
              })
            : await generateImage({
                apiKey: geminiApiKey,
                model: geminiModel,
                prompt,
                referenceImage: refImage,
              });

        if (result.success && result.imageBase64) {
          const description = result.description || `Generated: ${prompt.substring(0, 60)}`;
          writeImageEvent(result.imageBase64, description);

          return {
            content: [
              {
                type: "text" as const,
                text:
                  `Image generated successfully and sent to the Unity Editor.\n` +
                  `Backend: ${backend}\n` +
                  `Prompt: "${prompt}"\n` +
                  (result.description ? `Description: ${result.description}\n` : "") +
                  `The user can now preview and save it to their project from the AI Chat window.`,
              },
            ],
          };
        }

        return {
          content: [
            {
              type: "text" as const,
              text: `Image generation failed: ${result.error || "Unknown error"}`,
            },
          ],
          isError: true,
        };
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        return {
          content: [
            { type: "text" as const, text: `Image generation error: ${msg}` },
          ],
          isError: true,
        };
      }
    }
  );

  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[unity-agent-image] MCP stdio server ready");
}

main().catch((err) => {
  console.error(`[unity-agent-image] Fatal: ${err}`);
  process.exit(1);
});
