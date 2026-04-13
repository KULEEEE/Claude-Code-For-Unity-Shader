import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { UnityBridge } from "../unity-bridge.js";

const RT_TIMEOUT = 30000;

/**
 * Visual evidence: render target snapshot at a specific Frame Debugger event.
 * Expensive (PNG base64 payload) — the AI should call this sparingly,
 * only when it wants to verify an effect visually.
 */
export function registerFrameDebugRtTool(
  server: McpServer,
  bridge: UnityBridge
): void {
  server.tool(
    "framedebug_rt_snapshot",
    "Capture the active render target after a specific Frame Debugger event as a PNG (base64). Use this when you want VISUAL confirmation of what the GPU drew up to that event — e.g. 'did the shadow pass actually write anything?'. Expensive; call only when necessary.",
    {
      eventIndex: z
        .number()
        .int()
        .min(0)
        .describe(
          "Event index to stop at (the RT is captured after this event completes)."
        ),
      maxWidth: z
        .number()
        .int()
        .min(32)
        .max(4096)
        .default(512)
        .describe(
          "Max width in pixels for the returned PNG (height scales to preserve aspect). Default 512 keeps payload ~<300KB."
        ),
    },
    async ({ eventIndex, maxWidth }) => {
      try {
        const result = await bridge.request(
          "framedebug/rt",
          { eventIndex, maxWidth },
          RT_TIMEOUT
        );
        // Return as image if we got a PNG back, otherwise as text (error/metadata)
        const r = result as
          | {
              data?: string;
              format?: string;
              width?: number;
              height?: number;
              bytes?: number;
              error?: string;
              detail?: string;
            }
          | undefined;

        if (r?.data && r.format === "png-base64") {
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify(
                  {
                    eventIndex,
                    width: r.width,
                    height: r.height,
                    bytes: r.bytes,
                  },
                  null,
                  2
                ),
              },
              {
                type: "image" as const,
                data: r.data,
                mimeType: "image/png",
              },
            ],
          };
        }

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(r ?? { error: "No response from Unity" }, null, 2),
            },
          ],
        };
      } catch (err) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Error fetching RT snapshot: ${err instanceof Error ? err.message : String(err)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
