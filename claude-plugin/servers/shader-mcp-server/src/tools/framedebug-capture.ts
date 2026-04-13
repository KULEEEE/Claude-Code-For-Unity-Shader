import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const CAPTURE_TIMEOUT = 30000;

/**
 * Frame Debugger — overview tool.
 *
 * Tiki-taka pattern: this returns a lightweight event list so the AI can
 * survey the whole frame without burning tokens, then drill into specific
 * events via framedebug_event_detail / framedebug_event_shader / framedebug_rt_snapshot.
 */
export function registerFrameDebugCaptureTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_capture",
    "Enable Unity's Frame Debugger and capture the current frame. Returns a compact event overview (type, shader, draw counts) for the AI to scan — use framedebug_event_detail / framedebug_rt_snapshot to drill into specific events by index. Call framedebug_disable when done.",
    {
      maxEvents: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Maximum events to include in overview (0 = all)."),
      includeShaders: z
        .boolean()
        .default(false)
        .describe(
          "Include shader/pass name in each overview entry (costs extra reflection calls per event)."
        ),
    },
    async ({ maxEvents, includeShaders }) => {
      try {
        const result = await bridge.request(
          "framedebug/capture",
          { maxEvents, includeShaders },
          CAPTURE_TIMEOUT
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
              text: `Error capturing frame: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );

  server.tool(
    "framedebug_disable",
    "Disable Unity's Frame Debugger (releases resources, resumes normal rendering).",
    {},
    async () => {
      try {
        const result = await bridge.request("framedebug/disable", {}, 10000);
        return {
          content: [
            { type: "text" as const, text: JSON.stringify(result ?? {}, null, 2) },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error disabling frame debugger: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );

  server.tool(
    "framedebug_status",
    "Check the current Frame Debugger state without triggering a capture — returns {available, enabled, eventCount, isPlaying, unityVersion}.",
    {},
    async () => {
      try {
        const result = await bridge.request("framedebug/status", {}, 5000);
        return {
          content: [
            { type: "text" as const, text: JSON.stringify(result ?? {}, null, 2) },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error getting status: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
