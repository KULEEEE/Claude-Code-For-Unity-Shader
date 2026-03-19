import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ShaderLspClient } from "../lsp-client.js";

export function registerLspHoverTool(
  server: McpServer,
  lspClient: ShaderLspClient
): void {
  server.tool(
    "shader_hover",
    "Get type and documentation info for a shader symbol at a specific position. " +
      "Useful for understanding what a function, variable, or keyword does in ShaderLab/HLSL code.",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader file (e.g., Assets/Shaders/Character.shader)"
        ),
      line: z
        .number()
        .int()
        .min(0)
        .describe("Zero-based line number"),
      character: z
        .number()
        .int()
        .min(0)
        .describe("Zero-based character offset in the line"),
      content: z
        .string()
        .optional()
        .describe(
          "Optional: shader source code content. If not provided, the file will be read from disk."
        ),
    },
    async ({ shaderPath, line, character, content }) => {
      try {
        const result = await lspClient.hover(shaderPath, line, character, content);

        if (!result) {
          return {
            content: [
              {
                type: "text" as const,
                text: "No hover information available at this position.",
              },
            ],
          };
        }

        // Format hover contents
        let hoverText: string;
        if (typeof result.contents === "string") {
          hoverText = result.contents;
        } else if ("kind" in result.contents) {
          hoverText = result.contents.value;
        } else if (Array.isArray(result.contents)) {
          hoverText = result.contents
            .map((c) => (typeof c === "string" ? c : c.value))
            .join("\n\n");
        } else {
          hoverText = String(result.contents);
        }

        return {
          content: [
            {
              type: "text" as const,
              text: hoverText,
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error getting hover info: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
