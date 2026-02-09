import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerShaderIncludesResource(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.resource(
    "shader-includes",
    "unity://shader/includes",
    {
      description:
        "List of .cginc/.hlsl include files in the project with their contents",
      mimeType: "application/json",
    },
    async () => {
      try {
        const result = await bridge.request("shader/includes");
        return {
          contents: [
            {
              uri: "unity://shader/includes",
              mimeType: "application/json",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          contents: [
            {
              uri: "unity://shader/includes",
              mimeType: "text/plain",
              text: `Error: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
        };
      }
    }
  );
}
