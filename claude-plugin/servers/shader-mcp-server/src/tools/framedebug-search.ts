import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const SEARCH_TIMEOUT = 30000;

/**
 * Frame Debugger — targeted event query.
 *
 * Filters the frame's events by predicate and returns only matches (light
 * summaries + indices). Lets the AI skip the overview dump and zoom straight
 * to suspect events — e.g. "all Draws using MyShader with keyword _FOG"
 * or "every batch break in this frame".
 */
export function registerFrameDebugSearchTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_search",
    "Search frame events by predicate and return matching indices + light summaries. Combine filters (all AND'd): shader name substring, pass name substring, required shader keyword, event type substring (e.g. 'Draw'), min vertex/instance count, or only-batch-breaks. Use after framedebug_summary spots something interesting, then feed matching indices into framedebug_event_detail / framedebug_compare / framedebug_rt_snapshot.",
    {
      shaderNameContains: z
        .string()
        .optional()
        .describe("Case-insensitive substring match on shaderName (e.g. 'Universal', 'MyProject/')."),
      passNameContains: z
        .string()
        .optional()
        .describe("Case-insensitive substring match on passName (e.g. 'ShadowCaster', 'Forward')."),
      keyword: z
        .string()
        .optional()
        .describe("Only match events whose shader keyword array contains this substring (e.g. '_FOG_LINEAR', '_MAIN_LIGHT_SHADOWS')."),
      eventType: z
        .string()
        .optional()
        .describe("Substring match on FrameEventType (e.g. 'Draw', 'SetRenderTarget', 'ResolveRT', 'Clear')."),
      minVertexCount: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Minimum vertex count to include (useful to find heavy draws)."),
      minInstanceCount: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Minimum instance count (>0 finds instanced draws; >1 filters out non-instanced)."),
      batchBreaks: z
        .boolean()
        .default(false)
        .describe("When true, only return events that Unity marked as a batch break."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(512)
        .default(64)
        .describe("Max matches to return. 512 is the hard ceiling."),
    },
    async (filters) => {
      try {
        const result = await bridge.request(
          "framedebug/search",
          filters,
          SEARCH_TIMEOUT
        );
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
              text: `Error searching events: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
