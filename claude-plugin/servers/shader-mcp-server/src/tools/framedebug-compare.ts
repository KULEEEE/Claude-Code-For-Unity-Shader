import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const COMPARE_TIMEOUT = 15000;

/**
 * Frame Debugger — two-event diff.
 *
 * Answers "why does event B behave differently from event A?" — shader/pass
 * change, keyword set delta (added/removed), render-state diff, RT change,
 * geometry deltas, and whether B newly became a batch break.
 */
export function registerFrameDebugCompareTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_compare",
    "Diff two Frame Debugger events. Returns shader/pass change flags, keyword set diff (added/removed), render-state diff (raster/blend/depth/stencil fields that differ), RT dimension change, geometry deltas, and batch-break transition. Use this to answer 'why did this drawcall batch-break from the previous one' or 'what changed between these two passes'. Requires a prior framedebug_capture / framedebug_summary.",
    {
      indexA: z
        .number()
        .int()
        .min(0)
        .describe("Earlier (baseline) event index."),
      indexB: z
        .number()
        .int()
        .min(0)
        .describe("Later (comparison) event index."),
    },
    async ({ indexA, indexB }) => {
      try {
        const result = await bridge.request(
          "framedebug/compare",
          { indexA, indexB },
          COMPARE_TIMEOUT
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
              text: `Error comparing events: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
