import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { UnityBridge } from "./unity-bridge.js";
import { ShaderLspClient } from "./lsp-client.js";
import { handleAIQuery } from "./ai-handler.js";

// Tools
import { registerShaderCompileTool } from "./tools/shader-compile.js";
import { registerShaderAnalyzeTools } from "./tools/shader-analyze.js";
import { registerShaderVariantsTools } from "./tools/shader-variants.js";
import { registerShaderPropertiesTools } from "./tools/shader-properties.js";
import { registerMaterialInfoTools } from "./tools/material-info.js";

// LSP Tools
import { registerLspHoverTool } from "./tools/lsp-hover.js";
import { registerLspCompletionTool } from "./tools/lsp-completion.js";
import { registerLspSignatureTool } from "./tools/lsp-signature.js";
import { registerLspDiagnosticsTool } from "./tools/lsp-diagnostics.js";

// Resources
import { registerPipelineInfoResource } from "./resources/pipeline-info.js";
import { registerShaderIncludesResource } from "./resources/shader-includes.js";
import { registerShaderKeywordsResource } from "./resources/shader-keywords.js";
import { registerEditorPlatformResource } from "./resources/editor-platform.js";

async function main(): Promise<void> {
  const server = new McpServer({
    name: "unity-shader-tools",
    version: "0.1.3",
  });

  const bridge = new UnityBridge("ws://localhost:8090");
  const lspClient = new ShaderLspClient();

  // Register all tools
  registerShaderCompileTool(server, bridge);
  registerShaderAnalyzeTools(server, bridge);
  registerShaderVariantsTools(server, bridge);
  registerShaderPropertiesTools(server, bridge);
  registerMaterialInfoTools(server, bridge);

  // Register LSP tools (lazy — shader-ls starts on first use)
  registerLspHoverTool(server, lspClient);
  registerLspCompletionTool(server, lspClient);
  registerLspSignatureTool(server, lspClient);
  registerLspDiagnosticsTool(server, lspClient);

  // Register all resources
  registerPipelineInfoResource(server, bridge);
  registerShaderIncludesResource(server, bridge);
  registerShaderKeywordsResource(server, bridge);
  registerEditorPlatformResource(server, bridge);

  // Register AI query handler (Unity → MCP → Claude CLI → MCP → Unity)
  bridge.onMessage(async (msg) => {
    if (msg.method !== "ai/query") return;

    const id = msg.id as string;
    const params = msg.params as { prompt?: string; shaderContext?: string } | undefined;

    if (!id || !params?.prompt) {
      console.error("[ShaderMCP] Invalid AI query: missing id or prompt");
      return;
    }

    console.error(`[ShaderMCP] AI query received (id=${id}): ${(params.prompt as string).substring(0, 80)}...`);

    try {
      const result = await handleAIQuery({
        prompt: params.prompt,
        shaderContext: params.shaderContext,
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
      "[ShaderMCP] Initial connection to Unity failed. Will retry automatically."
    );
  });

  // Start MCP server on stdio
  const transport = new StdioServerTransport();
  await server.connect(transport);

  console.error("[ShaderMCP] MCP server started on stdio");

  // Cleanup on exit
  process.on("SIGINT", async () => {
    await lspClient.shutdown();
    bridge.disconnect();
    process.exit(0);
  });

  process.on("SIGTERM", async () => {
    await lspClient.shutdown();
    bridge.disconnect();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error(`[ShaderMCP] Fatal error: ${err}`);
  process.exit(1);
});
