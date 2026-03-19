import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerReadProjectFileTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "read_project_file",
    "Read a file from the Unity project. Use relative paths from the project root (e.g., 'Assets/Scripts/MyScript.cs').",
    {
      filePath: z
        .string()
        .describe(
          "Path to the file relative to Unity project root (e.g., Assets/Scripts/PlayerController.cs)"
        ),
    },
    async ({ filePath }) => {
      try {
        const result = await bridge.request("project/readFile", {
          filePath,
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
              text: `Error reading file: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
