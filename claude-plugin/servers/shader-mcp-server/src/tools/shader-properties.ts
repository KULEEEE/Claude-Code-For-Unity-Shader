import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerShaderPropertiesTools(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "get_shader_properties",
    "Get the list of properties defined in a shader (name, type, default value, attributes)",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader asset (e.g., Assets/Shaders/Character.shader)"
        ),
    },
    async ({ shaderPath }) => {
      try {
        const result = await bridge.request("shader/properties", {
          shaderPath,
        });
        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error getting shader properties: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
