#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { UnityBridge } from "./unity-bridge.js";

// Tools
import { registerShaderCompileTool } from "./tools/shader-compile.js";
import { registerShaderAnalyzeTools } from "./tools/shader-analyze.js";
import { registerShaderVariantsTools } from "./tools/shader-variants.js";
import { registerShaderPropertiesTools } from "./tools/shader-properties.js";
import { registerMaterialInfoTools } from "./tools/material-info.js";

// Resources
import { registerPipelineInfoResource } from "./resources/pipeline-info.js";
import { registerShaderIncludesResource } from "./resources/shader-includes.js";
import { registerShaderKeywordsResource } from "./resources/shader-keywords.js";
import { registerEditorPlatformResource } from "./resources/editor-platform.js";

async function main(): Promise<void> {
  const server = new McpServer({
    name: "unity-shader-tools",
    version: "0.1.0",
  });

  const bridge = new UnityBridge("ws://localhost:8090");

  // Register all tools
  registerShaderCompileTool(server, bridge);
  registerShaderAnalyzeTools(server, bridge);
  registerShaderVariantsTools(server, bridge);
  registerShaderPropertiesTools(server, bridge);
  registerMaterialInfoTools(server, bridge);

  // Register all resources
  registerPipelineInfoResource(server, bridge);
  registerShaderIncludesResource(server, bridge);
  registerShaderKeywordsResource(server, bridge);
  registerEditorPlatformResource(server, bridge);

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
  process.on("SIGINT", () => {
    bridge.disconnect();
    process.exit(0);
  });

  process.on("SIGTERM", () => {
    bridge.disconnect();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error(`[ShaderMCP] Fatal error: ${err}`);
  process.exit(1);
});
