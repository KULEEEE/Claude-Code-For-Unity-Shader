import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const SUMMARY_TIMEOUT = 45000;

/**
 * Frame Debugger — bird's-eye aggregate.
 *
 * Unlike framedebug_capture (which enumerates every event), this groups the
 * frame into digestible buckets: per-shader stats, event-type histogram,
 * RT transitions, batch-break causes, and top-N hotspots. Use this as the
 * first turn of a tiki-taka debug session — then drill in with
 * framedebug_search → framedebug_event_detail / framedebug_rt_snapshot.
 */
export function registerFrameDebugSummaryTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_summary",
    "Get an aggregate view of the current frame WITHOUT enumerating every event. Returns per-shader stats, event-type histogram, RT transition ranges, batch-break cause histogram, and top-N hotspots ranked by vertex×instance cost. Enables the Frame Debugger automatically if it's not running. Use this first to answer 'what's expensive in this frame?' before drilling in with framedebug_search / framedebug_event_detail.",
    {
      topHotspots: z
        .number()
        .int()
        .min(0)
        .max(64)
        .default(8)
        .describe("How many top-cost events to list as hotspots (0 = default 8)."),
      includeShaders: z
        .boolean()
        .default(true)
        .describe(
          "Include per-shader aggregation and RT transitions. Forces a deep sweep through GetFrameEventData; disable for very large frames (>1024 events) if summary feels slow."
        ),
    },
    async ({ topHotspots, includeShaders }) => {
      try {
        const result = await bridge.request(
          "framedebug/summary",
          { topHotspots, includeShaders },
          SUMMARY_TIMEOUT
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
              text: `Error fetching summary: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
