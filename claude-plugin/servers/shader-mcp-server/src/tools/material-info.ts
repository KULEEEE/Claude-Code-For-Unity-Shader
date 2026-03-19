import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerMaterialInfoTools(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "get_material_info",
    "Get detailed information about a material (shader, property values, keywords)",
    {
      materialPath: z
        .string()
        .describe(
          "Path to the material asset (e.g., Assets/Materials/Character.mat)"
        ),
    },
    async ({ materialPath }) => {
      try {
        const result = await bridge.request("material/info", { materialPath });
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
              text: `Error getting material info: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );

  server.tool(
    "get_shader_logs",
    "Get shader-related console log entries from Unity Editor",
    {
      severity: z
        .enum(["error", "warning", "all"])
        .optional()
        .default("all")
        .describe("Filter logs by severity level"),
    },
    async ({ severity }) => {
      try {
        const result = await bridge.request("editor/logs", { severity });
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
              text: `Error getting shader logs: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
