import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

export function registerGetUnityErrorsTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "get_unity_errors",
    "Get current Unity console errors and optionally warnings. Returns error messages, stack traces, source file locations, and whether they are compile errors.",
    {
      includeWarnings: z
        .boolean()
        .optional()
        .describe("Include warnings in addition to errors (default: false)"),
      limit: z
        .number()
        .optional()
        .describe("Maximum number of errors to return (default: 50)"),
    },
    async ({ includeWarnings, limit }) => {
      try {
        const result = await bridge.request("console/getErrors", {
          includeWarnings: includeWarnings ?? false,
          limit: limit ?? 50,
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
              text: `Error getting Unity errors: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
