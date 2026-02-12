import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerShaderAnalyzeTools(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "analyze_shader_variants",
    "Analyze shader keyword combinations and variant count",
    {
      shaderPath: z
        .string()
        .describe(
          "Path to the shader asset (e.g., Assets/Shaders/Character.shader)"
        ),
    },
    async ({ shaderPath }) => {
      try {
        const result = await bridge.request("shader/variants", { shaderPath });
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
              text: `Error analyzing shader variants: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );

  server.tool(
    "list_shaders",
    "List all shaders in the Unity project",
    {
      filter: z
        .string()
        .optional()
        .describe("Optional filter string to search shader names or paths"),
    },
    async ({ filter }) => {
      try {
        const result = await bridge.request("shader/list", {
          filter: filter ?? "",
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
              text: `Error listing shaders: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
