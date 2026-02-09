import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerShaderKeywordsResource(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.resource(
    "shader-keywords",
    "unity://shader/keywords",
    {
      description:
        "All global and local shader keywords currently used across the project",
      mimeType: "application/json",
    },
    async () => {
      try {
        const result = await bridge.request("material/keywords");
        return {
          contents: [
            {
              uri: "unity://shader/keywords",
              mimeType: "application/json",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          contents: [
            {
              uri: "unity://shader/keywords",
              mimeType: "text/plain",
              text: `Error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
        };
      }
    }
  );
}
