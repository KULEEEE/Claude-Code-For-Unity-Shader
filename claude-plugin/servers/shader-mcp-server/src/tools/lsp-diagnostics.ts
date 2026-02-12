import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ShaderLspClient } from "../lsp-client.js";

export function registerLspDiagnosticsTool(
  server: McpServer,
  lspClient: ShaderLspClient
): void {
  server.tool(
    "shader_diagnostics",
    "Get diagnostics (errors, warnings) for a shader file from the language server. " +
      "Note: shader-ls diagnostics support is limited in current versions. " +
      "For full compilation diagnostics, use compile_shader instead.",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader file (e.g., Assets/Shaders/Character.shader)"
        ),
      content: z
        .string()
        .optional()
        .describe(
          "Optional: shader source code content. If not provided, the file will be read from disk."
        ),
    },
    async ({ shaderPath, content }) => {
      try {
        // Ensure the LSP server is running (validates shader-ls availability)
        await lspClient.ensureRunning();

        return {
          content: [
            {
              type: "text" as const,
              text:
                "shader-ls does not yet support pull-based diagnostics.\n" +
                "This feature will be available in a future version of shader-ls.\n\n" +
                "Alternative: Use the `compile_shader` tool for Unity-based compilation diagnostics.",
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
