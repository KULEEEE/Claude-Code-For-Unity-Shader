import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const EVENT_TIMEOUT = 15000;

/**
 * Full per-event detail from the Frame Debugger: bindings, shader props,
 * render state (raster/blend/depth/stencil), RT dimensions, geometry counts.
 * Call after framedebug_capture, using an index from its event list.
 */
export function registerFrameDebugEventTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_event_detail",
    "Get the full state for a single Frame Debugger event: shader, keywords, render target, geometry, blend/depth/stencil state, and shader property snapshot. Requires a prior framedebug_capture. Use the event's `index` from the capture overview.",
    {
      eventIndex: z
        .number()
        .int()
        .min(0)
        .describe(
          "Event index from the framedebug_capture overview (0-based)."
        ),
    },
    async ({ eventIndex }) => {
      try {
        const result = await bridge.request(
          "framedebug/event",
          { eventIndex },
          EVENT_TIMEOUT
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
              text: `Error fetching event detail: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );

  server.tool(
    "framedebug_event_shader",
    "Get just the shader + keyword info for one Frame Debugger event (lighter than framedebug_event_detail). Useful when the AI wants to locate the asset or inspect variant keywords without pulling the full state dump.",
    {
      eventIndex: z
        .number()
        .int()
        .min(0)
        .describe("Event index from the framedebug_capture overview."),
    },
    async ({ eventIndex }) => {
      try {
        const result = await bridge.request(
          "framedebug/shader",
          { eventIndex },
          EVENT_TIMEOUT
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
              text: `Error fetching event shader: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
