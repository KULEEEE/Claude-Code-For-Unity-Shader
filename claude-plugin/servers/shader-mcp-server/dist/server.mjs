#!/usr/bin/env node

// build/index.js
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";

// build/unity-bridge.js
import WebSocket from "ws";
import { randomUUID } from "crypto";
var UnityBridge = class {
  ws = null;
  pendingRequests = /* @__PURE__ */ new Map();
  reconnectAttempts = 0;
  reconnectTimer = null;
  isConnecting = false;
  _isConnected = false;
  url;
  maxReconnectAttempts;
  reconnectInterval;
  defaultTimeout;
  constructor(url = "ws://localhost:8090", options) {
    this.url = url;
    this.maxReconnectAttempts = options?.maxReconnectAttempts ?? 10;
    this.reconnectInterval = options?.reconnectInterval ?? 3e3;
    this.defaultTimeout = options?.defaultTimeout ?? 1e4;
  }
  get isConnected() {
    return this._isConnected && this.ws?.readyState === WebSocket.OPEN;
  }
  /**
   * Connect to Unity WebSocket server (non-blocking).
   */
  async connect() {
    if (this.isConnecting || this.isConnected)
      return;
    this.isConnecting = true;
    return new Promise((resolve) => {
      try {
        this.ws = new WebSocket(this.url);
        this.ws.on("open", () => {
          this._isConnected = true;
          this.isConnecting = false;
          this.reconnectAttempts = 0;
          console.error("[ShaderMCP] Connected to Unity");
          resolve();
        });
        this.ws.on("message", (data) => {
          this.handleMessage(data.toString());
        });
        this.ws.on("close", () => {
          this._isConnected = false;
          this.isConnecting = false;
          console.error("[ShaderMCP] Disconnected from Unity");
          this.scheduleReconnect();
        });
        this.ws.on("error", (err) => {
          this.isConnecting = false;
          console.error(`[ShaderMCP] WebSocket error: ${err.message}`);
          resolve();
        });
      } catch (err) {
        this.isConnecting = false;
        console.error(`[ShaderMCP] Connection failed: ${err}`);
        this.scheduleReconnect();
        resolve();
      }
    });
  }
  /**
   * Send a request to Unity and wait for the response.
   */
  async request(method, params = {}, timeoutMs) {
    if (!this.isConnected) {
      throw new Error("Not connected to Unity. Please ensure the Shader MCP Server is running in Unity Editor (Tools > Shader MCP > Server Window).");
    }
    const id = randomUUID();
    const timeout = timeoutMs ?? this.defaultTimeout;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        reject(new Error(`Request timed out after ${timeout}ms: ${method}`));
      }, timeout);
      this.pendingRequests.set(id, { resolve, reject, timer });
      const message = JSON.stringify({ id, method, params });
      this.ws.send(message);
    });
  }
  /**
   * Disconnect from Unity.
   */
  disconnect() {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this._isConnected = false;
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.reject(new Error("Disconnected"));
    }
    this.pendingRequests.clear();
  }
  handleMessage(raw) {
    try {
      const msg = JSON.parse(raw);
      const id = msg.id;
      if (!id || !this.pendingRequests.has(id)) {
        console.error(`[ShaderMCP] Received message with unknown id: ${id}`);
        return;
      }
      const pending = this.pendingRequests.get(id);
      this.pendingRequests.delete(id);
      clearTimeout(pending.timer);
      if (msg.error) {
        pending.reject(new Error(`Unity error (${msg.error.code}): ${msg.error.message}`));
      } else {
        pending.resolve(msg.result);
      }
    } catch (err) {
      console.error(`[ShaderMCP] Failed to parse message: ${err}`);
    }
  }
  scheduleReconnect() {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      console.error("[ShaderMCP] Max reconnect attempts reached");
      return;
    }
    if (this.reconnectTimer)
      return;
    this.reconnectAttempts++;
    console.error(`[ShaderMCP] Reconnecting in ${this.reconnectInterval}ms (attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts})`);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect().catch(() => {
      });
    }, this.reconnectInterval);
  }
};

// build/tools/shader-compile.js
import { z } from "zod";
var COMPILE_TIMEOUT = 3e4;
function registerShaderCompileTool(server, bridge) {
  server.tool("compile_shader", "Compile a Unity shader and return errors, warnings, and variant count", {
    shaderPath: z.string().describe("Path to the shader asset (e.g., Assets/Shaders/Character.shader)")
  }, async ({ shaderPath }) => {
    try {
      const result = await bridge.request("shader/compile", { shaderPath }, COMPILE_TIMEOUT);
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error compiling shader: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/shader-analyze.js
import { z as z2 } from "zod";
function registerShaderAnalyzeTools(server, bridge) {
  server.tool("analyze_shader_variants", "Analyze shader keyword combinations and variant count", {
    shaderPath: z2.string().describe("Path to the shader asset (e.g., Assets/Shaders/Character.shader)")
  }, async ({ shaderPath }) => {
    try {
      const result = await bridge.request("shader/variants", { shaderPath });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error analyzing shader variants: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
  server.tool("list_shaders", "List all shaders in the Unity project", {
    filter: z2.string().optional().describe("Optional filter string to search shader names or paths")
  }, async ({ filter }) => {
    try {
      const result = await bridge.request("shader/list", {
        filter: filter ?? ""
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error listing shaders: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/shader-variants.js
import { z as z3 } from "zod";
function registerShaderVariantsTools(server, bridge) {
  server.tool("get_shader_code", "Read shader source code with optional include file resolution", {
    shaderPath: z3.string().describe("Path to the shader file (e.g., Assets/Shaders/Character.shader)"),
    resolveIncludes: z3.boolean().optional().default(false).describe("Whether to resolve and include referenced .cginc/.hlsl files")
  }, async ({ shaderPath, resolveIncludes }) => {
    try {
      const result = await bridge.request("shader/getCode", {
        shaderPath,
        resolveIncludes
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error reading shader code: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/shader-properties.js
import { z as z4 } from "zod";
function registerShaderPropertiesTools(server, bridge) {
  server.tool("get_shader_properties", "Get the list of properties defined in a shader (name, type, default value, attributes)", {
    shaderPath: z4.string().describe("Path to the shader asset (e.g., Assets/Shaders/Character.shader)")
  }, async ({ shaderPath }) => {
    try {
      const result = await bridge.request("shader/properties", {
        shaderPath
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting shader properties: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/material-info.js
import { z as z5 } from "zod";
function registerMaterialInfoTools(server, bridge) {
  server.tool("get_material_info", "Get detailed information about a material (shader, property values, keywords)", {
    materialPath: z5.string().describe("Path to the material asset (e.g., Assets/Materials/Character.mat)")
  }, async ({ materialPath }) => {
    try {
      const result = await bridge.request("material/info", { materialPath });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting material info: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
  server.tool("get_shader_logs", "Get shader-related console log entries from Unity Editor", {
    severity: z5.enum(["error", "warning", "all"]).optional().default("all").describe("Filter logs by severity level")
  }, async ({ severity }) => {
    try {
      const result = await bridge.request("editor/logs", { severity });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(result ?? { error: "No response from Unity" }, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting shader logs: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/resources/pipeline-info.js
function registerPipelineInfoResource(server, bridge) {
  server.resource("pipeline-info", "unity://pipeline/info", {
    description: "Current render pipeline type and settings (Built-in, URP, or HDRP)",
    mimeType: "application/json"
  }, async () => {
    try {
      const result = await bridge.request("pipeline/info");
      return {
        contents: [
          {
            uri: "unity://pipeline/info",
            mimeType: "application/json",
            text: JSON.stringify(result, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        contents: [
          {
            uri: "unity://pipeline/info",
            mimeType: "text/plain",
            text: `Error: ${err instanceof Error ? err.message : String(err)}`
          }
        ]
      };
    }
  });
}

// build/resources/shader-includes.js
function registerShaderIncludesResource(server, bridge) {
  server.resource("shader-includes", "unity://shader/includes", {
    description: "List of .cginc/.hlsl include files in the project with their contents",
    mimeType: "application/json"
  }, async () => {
    try {
      const result = await bridge.request("shader/includes");
      return {
        contents: [
          {
            uri: "unity://shader/includes",
            mimeType: "application/json",
            text: JSON.stringify(result, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        contents: [
          {
            uri: "unity://shader/includes",
            mimeType: "text/plain",
            text: `Error: ${err instanceof Error ? err.message : String(err)}`
          }
        ]
      };
    }
  });
}

// build/resources/shader-keywords.js
function registerShaderKeywordsResource(server, bridge) {
  server.resource("shader-keywords", "unity://shader/keywords", {
    description: "All global and local shader keywords currently used across the project",
    mimeType: "application/json"
  }, async () => {
    try {
      const result = await bridge.request("material/keywords");
      return {
        contents: [
          {
            uri: "unity://shader/keywords",
            mimeType: "application/json",
            text: JSON.stringify(result, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        contents: [
          {
            uri: "unity://shader/keywords",
            mimeType: "text/plain",
            text: `Error: ${err instanceof Error ? err.message : String(err)}`
          }
        ]
      };
    }
  });
}

// build/resources/editor-platform.js
function registerEditorPlatformResource(server, bridge) {
  server.resource("editor-platform", "unity://editor/platform", {
    description: "Current build target platform, Graphics API, and Unity version info",
    mimeType: "application/json"
  }, async () => {
    try {
      const result = await bridge.request("editor/platform");
      return {
        contents: [
          {
            uri: "unity://editor/platform",
            mimeType: "application/json",
            text: JSON.stringify(result, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        contents: [
          {
            uri: "unity://editor/platform",
            mimeType: "text/plain",
            text: `Error: ${err instanceof Error ? err.message : String(err)}`
          }
        ]
      };
    }
  });
}

// build/index.js
async function main() {
  const server = new McpServer({
    name: "unity-shader-tools",
    version: "0.1.0"
  });
  const bridge = new UnityBridge("ws://localhost:8090");
  registerShaderCompileTool(server, bridge);
  registerShaderAnalyzeTools(server, bridge);
  registerShaderVariantsTools(server, bridge);
  registerShaderPropertiesTools(server, bridge);
  registerMaterialInfoTools(server, bridge);
  registerPipelineInfoResource(server, bridge);
  registerShaderIncludesResource(server, bridge);
  registerShaderKeywordsResource(server, bridge);
  registerEditorPlatformResource(server, bridge);
  bridge.connect().catch(() => {
    console.error("[ShaderMCP] Initial connection to Unity failed. Will retry automatically.");
  });
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[ShaderMCP] MCP server started on stdio");
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
