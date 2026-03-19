import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerWriteProjectFileTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "write_project_file",
    "Write or modify a file in the Unity project. The file will be created if it doesn't exist. Unity will automatically recompile after the write.",
    {
      filePath: z
        .string()
        .describe(
          "Path to the file relative to Unity project root (e.g., Assets/Scripts/PlayerController.cs)"
        ),
      content: z.string().describe("The full content to write to the file"),
    },
    async ({ filePath, content }) => {
      try {
        const result = await bridge.request(
          "project/writeFile",
          { filePath, content },
          30000 // 30s timeout for write + recompile
        );
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
              text: `Error writing file: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
