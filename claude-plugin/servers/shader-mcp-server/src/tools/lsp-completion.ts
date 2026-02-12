import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ShaderLspClient } from "../lsp-client.js";

export function registerLspCompletionTool(
  server: McpServer,
  lspClient: ShaderLspClient
): void {
  server.tool(
    "shader_completion",
    "Get code completion suggestions at a specific position in a shader file. " +
      "Returns a list of suggested completions for ShaderLab/HLSL code.",
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
        const result = await lspClient.completion(
          shaderPath,
          line,
          character,
          content
        );

        if (!result) {
          return {
            content: [
              {
                type: "text" as const,
                text: "No completions available at this position.",
              },
            ],
          };
        }

        // Normalize to array of CompletionItems
        const items = Array.isArray(result) ? result : result.items;

        if (!items || items.length === 0) {
          return {
            content: [
              {
                type: "text" as const,
                text: "No completions available at this position.",
              },
            ],
          };
        }

        // Format completion items
        const formatted = items.map((item) => ({
          label: item.label,
          kind: item.kind,
          detail: item.detail,
          documentation:
            typeof item.documentation === "string"
              ? item.documentation
              : item.documentation && "value" in item.documentation
                ? item.documentation.value
                : undefined,
          insertText: item.insertText,
        }));

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(formatted, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error getting completions: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
