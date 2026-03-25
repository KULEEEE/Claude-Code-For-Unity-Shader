import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";
import { generateImage } from "../gemini-handler.js";

/**
 * Stores the latest Gemini settings received from Unity.
 * Updated each time an ai/query message arrives with gemini config.
 */
export const geminiConfig = {
  apiKey: "",
  model: "gemini-2.5-flash-preview-image-generation",
  referenceImage: undefined as string | undefined,
};

export function registerGenerateImageTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "generate_image",
    "Generate an image using Google's Nano Banana (Gemini Image Generation). " +
      "Use this when the user asks to create, generate, or make an image, texture, sprite, icon, or visual asset. " +
      "The generated image will be displayed in the Unity Editor's AI Chat window where the user can save it to their project. " +
      "You should describe what you're generating and call this tool with a detailed prompt.",
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
        .describe(
          "Whether to include the user's reference image (if one is set in the UI). Default: true if available."
        ),
    },
    async ({ prompt, useReferenceImage }) => {
      if (!geminiConfig.apiKey) {
        return {
          content: [
            {
              type: "text" as const,
              text: "Error: Gemini API key is not configured. The user needs to set it in AI Chat > Settings panel.",
            },
          ],
          isError: true,
        };
      }

      try {
        const shouldUseRef = useReferenceImage !== false;
        const refImage =
          shouldUseRef && geminiConfig.referenceImage
            ? geminiConfig.referenceImage
            : undefined;

        const result = await generateImage({
          apiKey: geminiConfig.apiKey,
          model: geminiConfig.model,
          prompt,
          referenceImage: refImage,
        });

        if (result.success && result.imageBase64) {
          // Send the image to Unity for display
          bridge.sendRaw({
            method: "image/generated",
            imageData: result.imageBase64,
            description: result.description || `Generated: ${prompt.substring(0, 60)}`,
          });

          return {
            content: [
              {
                type: "text" as const,
                text:
                  `Image generated successfully and sent to the Unity Editor.\n` +
                  `Model: ${geminiConfig.model}\n` +
                  `Prompt: "${prompt}"\n` +
                  (result.description
                    ? `Description: ${result.description}\n`
                    : "") +
                  `The user can now preview and save it to their project from the AI Chat window.`,
              },
            ],
          };
        } else {
          return {
            content: [
              {
                type: "text" as const,
                text: `Image generation failed: ${result.error || "Unknown error"}`,
              },
            ],
            isError: true,
          };
        }
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Image generation error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
