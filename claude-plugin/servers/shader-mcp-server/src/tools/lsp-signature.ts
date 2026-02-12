import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ShaderLspClient } from "../lsp-client.js";

export function registerLspSignatureTool(
  server: McpServer,
  lspClient: ShaderLspClient
): void {
  server.tool(
    "shader_signature_help",
    "Get function signature help at a specific position in a shader file. " +
      "Useful when cursor is inside a function call to see parameter information.",
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
        const result = await lspClient.signatureHelp(
          shaderPath,
          line,
          character,
          content
        );

        if (!result || !result.signatures || result.signatures.length === 0) {
          return {
            content: [
              {
                type: "text" as const,
                text: "No signature help available at this position.",
              },
            ],
          };
        }

        // Format signature information
        const formatted = {
          activeSignature: result.activeSignature ?? 0,
          activeParameter: result.activeParameter ?? 0,
          signatures: result.signatures.map((sig) => ({
            label: sig.label,
            documentation:
              typeof sig.documentation === "string"
                ? sig.documentation
                : sig.documentation && "value" in sig.documentation
                  ? sig.documentation.value
                  : undefined,
            parameters: sig.parameters?.map((p) => ({
              label: p.label,
              documentation:
                typeof p.documentation === "string"
                  ? p.documentation
                  : p.documentation && "value" in p.documentation
                    ? p.documentation.value
                    : undefined,
            })),
          })),
        };

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
              text: `Error getting signature help: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
