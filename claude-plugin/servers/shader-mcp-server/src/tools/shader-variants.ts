import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerShaderVariantsTools(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "get_shader_code",
    "Read shader source code with optional include file resolution",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader file (e.g., Assets/Shaders/Character.shader)"
        ),
      resolveIncludes: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Whether to resolve and include referenced .cginc/.hlsl files"
        ),
    },
    async ({ shaderPath, resolveIncludes }) => {
      try {
        const result = await bridge.request("shader/getCode", {
          shaderPath,
          resolveIncludes,
        });
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
              text: `Error reading shader code: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
