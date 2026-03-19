import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const COMPILE_TIMEOUT = 30000; // 30 seconds for compilation

export function registerShaderCompileTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "compile_shader",
    "Compile a Unity shader and return errors, warnings, and variant count",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader asset (e.g., Assets/Shaders/Character.shader)"
        ),
    },
    async ({ shaderPath }) => {
      try {
        const result = await bridge.request(
          "shader/compile",
          { shaderPath },
          COMPILE_TIMEOUT
        );
        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error compiling shader: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
