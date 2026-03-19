import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerEditorPlatformResource(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.resource(
    "editor-platform",
    "unity://editor/platform",
    {
      description:
        "Current build target platform, Graphics API, and Unity version info",
      mimeType: "application/json",
    },
    async () => {
      try {
        const result = await bridge.request("editor/platform");
        return {
          contents: [
            {
              uri: "unity://editor/platform",
              mimeType: "application/json",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          contents: [
            {
              uri: "unity://editor/platform",
              mimeType: "text/plain",
              text: `Error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
        };
      }
    }
  );
}
