import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { UnityBridge } from "./unity-bridge.js";
import { handleAIQuery } from "./ai-handler.js";

// Tools
import { registerGetUnityErrorsTool } from "./tools/get-unity-errors.js";
import { registerReadProjectFileTool } from "./tools/read-project-file.js";
import { registerWriteProjectFileTool } from "./tools/write-project-file.js";
import { registerListProjectFilesTool } from "./tools/list-project-files.js";

async function main(): Promise<void> {
  const server = new McpServer({
    name: "unity-error-solver",
    version: "0.5.0",
  });

  const bridge = new UnityBridge("ws://localhost:8090");

  // Register error-solving tools
  registerGetUnityErrorsTool(server, bridge);
  registerReadProjectFileTool(server, bridge);
  registerWriteProjectFileTool(server, bridge);
  registerListProjectFilesTool(server, bridge);

  // Register AI query handler (Unity → MCP → Claude CLI → MCP → Unity)
  bridge.onMessage(async (msg) => {
    if (msg.method !== "ai/query") return;

    const id = msg.id as string;
    const params = msg.params as {
      prompt?: string;
      context?: string;
      language?: string;
      projectPath?: string;
    } | undefined;

    if (!id || !params?.prompt) {
      console.error("[UnityMCP] Invalid AI query: missing id or prompt");
      return;
    }

    console.error(
      `[UnityMCP] AI query received (id=${id}): ${(params.prompt as string).substring(0, 80)}...`
    );

    try {
      const result = await handleAIQuery({
        prompt: params.prompt,
        context: params.context,
        language: params.language,
        projectPath: params.projectPath,
        onChunk: (chunk: string) => {
          bridge.sendRaw({ method: "ai/chunk", id, chunk });
        },
        onStatus: (status: string) => {
          bridge.sendRaw({ method: "ai/status", id, status });
        },
      });

      if (result.success) {
        bridge.sendRaw({
          method: "ai/response",
          id,
          result: result.response,
        });
      } else {
        bridge.sendRaw({
          method: "ai/response",
          id,
          error: result.error,
        });
      }
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      bridge.sendRaw({
        method: "ai/response",
        id,
        error: `AI handler error: ${errMsg}`,
      });
    }
  });

  // Connect to Unity (non-blocking — server starts even if Unity is not running)
  bridge.connect().catch(() => {
    console.error(
      "[UnityMCP] Initial connection to Unity failed. Will retry automatically."
    );
  });

  // Start MCP server on stdio
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error("[UnityMCP] MCP server started on stdio");

  // Cleanup on exit
  process.on("SIGINT", async () => {
    bridge.disconnect();
    process.exit(0);
  });

  process.on("SIGTERM", async () => {
    bridge.disconnect();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error(`[UnityMCP] Fatal error: ${err}`);
  process.exit(1);
});
