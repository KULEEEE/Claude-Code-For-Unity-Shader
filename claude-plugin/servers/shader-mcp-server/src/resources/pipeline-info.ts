import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerPipelineInfoResource(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.resource(
    "pipeline-info",
    "unity://pipeline/info",
    {
      description:
        "Current render pipeline type and settings (Built-in, URP, or HDRP)",
      mimeType: "application/json",
    },
    async () => {
      try {
        const result = await bridge.request("pipeline/info");
        return {
          contents: [
            {
              uri: "unity://pipeline/info",
              mimeType: "application/json",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          contents: [
            {
              uri: "unity://pipeline/info",
              mimeType: "text/plain",
              text: `Error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
        };
      }
    }
  );
}
