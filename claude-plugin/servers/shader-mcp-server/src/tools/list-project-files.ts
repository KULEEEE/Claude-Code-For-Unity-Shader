import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerListProjectFilesTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "list_project_files",
    "List files in the Unity project directory. Useful for discovering scripts and assets related to an error.",
    {
      directory: z
        .string()
        .optional()
        .describe(
          "Directory to search (relative to project root, default: 'Assets')"
        ),
      pattern: z
        .string()
        .optional()
        .describe("File pattern to match (default: '*.cs')"),
    },
    async ({ directory, pattern }) => {
      try {
        const result = await bridge.request("project/listFiles", {
          directory: directory ?? "",
          pattern: pattern ?? "*.cs",
        });
        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(
                result ?? { error: "No response from Unity" },
                null,
                2
              ),
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error listing files: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
