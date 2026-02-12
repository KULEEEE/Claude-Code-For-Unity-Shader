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
    return new Promise((resolve2) => {
      try {
        this.ws = new WebSocket(this.url);
        this.ws.on("open", () => {
          this._isConnected = true;
          this.isConnecting = false;
          this.reconnectAttempts = 0;
          console.error("[ShaderMCP] Connected to Unity");
          resolve2();
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
          resolve2();
        });
      } catch (err) {
        this.isConnecting = false;
        console.error(`[ShaderMCP] Connection failed: ${err}`);
        this.scheduleReconnect();
        resolve2();
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
    return new Promise((resolve2, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        reject(new Error(`Request timed out after ${timeout}ms: ${method}`));
      }, timeout);
      this.pendingRequests.set(id, { resolve: resolve2, reject, timer });
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

// build/lsp-client.js
import { spawn, execSync } from "child_process";
import { pathToFileURL } from "url";
import { readFileSync } from "fs";
import { resolve } from "path";
import { createProtocolConnection, StreamMessageReader, StreamMessageWriter, InitializeRequest, InitializedNotification, DidOpenTextDocumentNotification, DidChangeTextDocumentNotification, HoverRequest, CompletionRequest, SignatureHelpRequest } from "vscode-languageserver-protocol/node.js";
var LSP_INIT_TIMEOUT = 15e3;
var LSP_REQUEST_TIMEOUT = 1e4;
var SHADER_LS_COMMAND = "shader-ls";
var ShaderLspClient = class {
  process = null;
  connection = null;
  openDocuments = /* @__PURE__ */ new Map();
  initPromise = null;
  isShuttingDown = false;
  projectRoot;
  constructor(projectRoot) {
    this.projectRoot = projectRoot ?? process.cwd();
  }
  /**
   * Ensure the LSP server is running. Called lazily on first tool use.
   */
  async ensureRunning() {
    if (this.connection && this.process && !this.process.killed) {
      return;
    }
    if (this.initPromise) {
      return this.initPromise;
    }
    this.initPromise = this.startServer();
    try {
      await this.initPromise;
    } finally {
      this.initPromise = null;
    }
  }
  async startServer() {
    let spawned = this.trySpawn();
    if (!spawned) {
      await this.autoInstall();
      spawned = this.trySpawn();
      if (!spawned) {
        throw new Error("Failed to start shader-ls after installation. Please verify the installation with: dotnet tool list -g");
      }
    }
    await this.initializeLsp();
  }
  trySpawn() {
    try {
      const isWindows = process.platform === "win32";
      const command = isWindows ? `${SHADER_LS_COMMAND}.exe` : SHADER_LS_COMMAND;
      this.process = spawn(command, ["--stdio"], {
        stdio: ["pipe", "pipe", "pipe"],
        env: { ...process.env },
        shell: isWindows
      });
      if (!this.process.pid) {
        this.process = null;
        return false;
      }
      this.process.on("exit", (code) => {
        if (!this.isShuttingDown) {
          console.error(`[ShaderMCP-LSP] shader-ls exited with code ${code}. Will restart on next request.`);
        }
        this.cleanup();
      });
      this.process.on("error", (err) => {
        console.error(`[ShaderMCP-LSP] shader-ls process error: ${err.message}`);
        this.cleanup();
      });
      this.process.stderr?.on("data", (data) => {
        console.error(`[shader-ls] ${data.toString().trim()}`);
      });
      return true;
    } catch {
      this.process = null;
      return false;
    }
  }
  async autoInstall() {
    console.error("[ShaderMCP-LSP] shader-ls not found. Attempting auto-install...");
    const dotnetCmd = process.platform === "win32" ? "where dotnet" : "which dotnet";
    try {
      execSync(dotnetCmd, { stdio: "pipe" });
    } catch {
      throw new Error("shader-ls is not installed and .NET SDK was not found.\nTo use LSP features, please:\n1. Install .NET 7.0+ SDK from https://dotnet.microsoft.com/download\n2. Run: dotnet tool install --global shader-ls\n\nNote: Existing shader MCP tools (compile, analyze, etc.) work without this.");
    }
    console.error("[ShaderMCP-LSP] Installing shader-ls via dotnet...");
    try {
      execSync("dotnet tool install --global shader-ls", {
        stdio: "pipe",
        timeout: 12e4
      });
      console.error("[ShaderMCP-LSP] shader-ls installed successfully.");
    } catch (err) {
      try {
        execSync("dotnet tool update --global shader-ls", {
          stdio: "pipe",
          timeout: 12e4
        });
        console.error("[ShaderMCP-LSP] shader-ls updated successfully.");
      } catch (updateErr) {
        throw new Error(`Failed to install shader-ls: ${err instanceof Error ? err.message : String(err)}
Please install manually: dotnet tool install --global shader-ls`);
      }
    }
  }
  async initializeLsp() {
    if (!this.process?.stdout || !this.process?.stdin) {
      throw new Error("shader-ls process has no stdio streams");
    }
    const reader = new StreamMessageReader(this.process.stdout);
    const writer = new StreamMessageWriter(this.process.stdin);
    this.connection = createProtocolConnection(reader, writer);
    this.connection.listen();
    const initParams = {
      processId: process.pid,
      capabilities: {
        textDocument: {
          hover: { contentFormat: ["markdown", "plaintext"] },
          completion: {
            completionItem: {
              snippetSupport: false,
              documentationFormat: ["markdown", "plaintext"]
            }
          },
          signatureHelp: {
            signatureInformation: {
              documentationFormat: ["markdown", "plaintext"]
            }
          }
        }
      },
      rootUri: pathToFileURL(this.projectRoot).toString(),
      workspaceFolders: [
        {
          uri: pathToFileURL(this.projectRoot).toString(),
          name: "workspace"
        }
      ]
    };
    const initResult = await Promise.race([
      this.connection.sendRequest(InitializeRequest.type, initParams),
      new Promise((_, reject) => setTimeout(() => reject(new Error("LSP initialization timed out")), LSP_INIT_TIMEOUT))
    ]);
    console.error(`[ShaderMCP-LSP] LSP initialized. Server: ${initResult.serverInfo?.name ?? "unknown"} ${initResult.serverInfo?.version ?? ""}`);
    await this.connection.sendNotification(InitializedNotification.type, {});
  }
  /**
   * Convert a Unity asset path to a file:// URI.
   */
  toUri(shaderPath) {
    const absPath = resolve(this.projectRoot, shaderPath);
    return pathToFileURL(absPath).toString();
  }
  /**
   * Open or update a document in the LSP server.
   */
  async openOrUpdateDocument(shaderPath, content) {
    await this.ensureRunning();
    const uri = this.toUri(shaderPath);
    const languageId = shaderPath.endsWith(".hlsl") ? "hlsl" : "shaderlab";
    const docContent = content ?? readFileSync(resolve(this.projectRoot, shaderPath), "utf-8");
    const existing = this.openDocuments.get(uri);
    if (!existing) {
      await this.connection.sendNotification(DidOpenTextDocumentNotification.type, {
        textDocument: {
          uri,
          languageId,
          version: 1,
          text: docContent
        }
      });
      this.openDocuments.set(uri, { uri, version: 1, content: docContent });
    } else if (existing.content !== docContent) {
      const newVersion = existing.version + 1;
      await this.connection.sendNotification(DidChangeTextDocumentNotification.type, {
        textDocument: { uri, version: newVersion },
        contentChanges: [{ text: docContent }]
      });
      this.openDocuments.set(uri, {
        uri,
        version: newVersion,
        content: docContent
      });
    }
    return uri;
  }
  /**
   * Get hover information at a position.
   */
  async hover(shaderPath, line, character, content) {
    const uri = await this.openOrUpdateDocument(shaderPath, content);
    return Promise.race([
      this.connection.sendRequest(HoverRequest.type, {
        textDocument: { uri },
        position: { line, character }
      }),
      new Promise((resolve2) => setTimeout(() => resolve2(null), LSP_REQUEST_TIMEOUT))
    ]);
  }
  /**
   * Get completion suggestions at a position.
   */
  async completion(shaderPath, line, character, content) {
    const uri = await this.openOrUpdateDocument(shaderPath, content);
    return Promise.race([
      this.connection.sendRequest(CompletionRequest.type, {
        textDocument: { uri },
        position: { line, character }
      }),
      new Promise((resolve2) => setTimeout(() => resolve2(null), LSP_REQUEST_TIMEOUT))
    ]);
  }
  /**
   * Get signature help at a position.
   */
  async signatureHelp(shaderPath, line, character, content) {
    const uri = await this.openOrUpdateDocument(shaderPath, content);
    return Promise.race([
      this.connection.sendRequest(SignatureHelpRequest.type, {
        textDocument: { uri },
        position: { line, character }
      }),
      new Promise((resolve2) => setTimeout(() => resolve2(null), LSP_REQUEST_TIMEOUT))
    ]);
  }
  /**
   * Gracefully shut down the LSP server.
   */
  async shutdown() {
    this.isShuttingDown = true;
    if (this.connection) {
      try {
        await Promise.race([
          this.connection.sendRequest("shutdown"),
          new Promise((resolve2) => setTimeout(resolve2, 3e3))
        ]);
        this.connection.sendNotification("exit");
      } catch {
      }
    }
    this.cleanup();
  }
  cleanup() {
    if (this.connection) {
      this.connection.dispose();
      this.connection = null;
    }
    if (this.process && !this.process.killed) {
      this.process.kill();
    }
    this.process = null;
    this.openDocuments.clear();
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

// build/tools/lsp-hover.js
import { z as z6 } from "zod";
function registerLspHoverTool(server, lspClient) {
  server.tool("shader_hover", "Get type and documentation info for a shader symbol at a specific position. Useful for understanding what a function, variable, or keyword does in ShaderLab/HLSL code.", {
    shaderPath: z6.string().describe("Path to the shader file (e.g., Assets/Shaders/Character.shader)"),
    line: z6.number().int().min(0).describe("Zero-based line number"),
    character: z6.number().int().min(0).describe("Zero-based character offset in the line"),
    content: z6.string().optional().describe("Optional: shader source code content. If not provided, the file will be read from disk.")
  }, async ({ shaderPath, line, character, content }) => {
    try {
      const result = await lspClient.hover(shaderPath, line, character, content);
      if (!result) {
        return {
          content: [
            {
              type: "text",
              text: "No hover information available at this position."
            }
          ]
        };
      }
      let hoverText;
      if (typeof result.contents === "string") {
        hoverText = result.contents;
      } else if ("kind" in result.contents) {
        hoverText = result.contents.value;
      } else if (Array.isArray(result.contents)) {
        hoverText = result.contents.map((c) => typeof c === "string" ? c : c.value).join("\n\n");
      } else {
        hoverText = String(result.contents);
      }
      return {
        content: [
          {
            type: "text",
            text: hoverText
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting hover info: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/lsp-completion.js
import { z as z7 } from "zod";
function registerLspCompletionTool(server, lspClient) {
  server.tool("shader_completion", "Get code completion suggestions at a specific position in a shader file. Returns a list of suggested completions for ShaderLab/HLSL code.", {
    shaderPath: z7.string().describe("Path to the shader file (e.g., Assets/Shaders/Character.shader)"),
    line: z7.number().int().min(0).describe("Zero-based line number"),
    character: z7.number().int().min(0).describe("Zero-based character offset in the line"),
    content: z7.string().optional().describe("Optional: shader source code content. If not provided, the file will be read from disk.")
  }, async ({ shaderPath, line, character, content }) => {
    try {
      const result = await lspClient.completion(shaderPath, line, character, content);
      if (!result) {
        return {
          content: [
            {
              type: "text",
              text: "No completions available at this position."
            }
          ]
        };
      }
      const items = Array.isArray(result) ? result : result.items;
      if (!items || items.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: "No completions available at this position."
            }
          ]
        };
      }
      const formatted = items.map((item) => ({
        label: item.label,
        kind: item.kind,
        detail: item.detail,
        documentation: typeof item.documentation === "string" ? item.documentation : item.documentation && "value" in item.documentation ? item.documentation.value : void 0,
        insertText: item.insertText
      }));
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(formatted, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting completions: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/lsp-signature.js
import { z as z8 } from "zod";
function registerLspSignatureTool(server, lspClient) {
  server.tool("shader_signature_help", "Get function signature help at a specific position in a shader file. Useful when cursor is inside a function call to see parameter information.", {
    shaderPath: z8.string().describe("Path to the shader file (e.g., Assets/Shaders/Character.shader)"),
    line: z8.number().int().min(0).describe("Zero-based line number"),
    character: z8.number().int().min(0).describe("Zero-based character offset in the line"),
    content: z8.string().optional().describe("Optional: shader source code content. If not provided, the file will be read from disk.")
  }, async ({ shaderPath, line, character, content }) => {
    try {
      const result = await lspClient.signatureHelp(shaderPath, line, character, content);
      if (!result || !result.signatures || result.signatures.length === 0) {
        return {
          content: [
            {
              type: "text",
              text: "No signature help available at this position."
            }
          ]
        };
      }
      const formatted = {
        activeSignature: result.activeSignature ?? 0,
        activeParameter: result.activeParameter ?? 0,
        signatures: result.signatures.map((sig) => ({
          label: sig.label,
          documentation: typeof sig.documentation === "string" ? sig.documentation : sig.documentation && "value" in sig.documentation ? sig.documentation.value : void 0,
          parameters: sig.parameters?.map((p) => ({
            label: p.label,
            documentation: typeof p.documentation === "string" ? p.documentation : p.documentation && "value" in p.documentation ? p.documentation.value : void 0
          }))
        }))
      };
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(formatted, null, 2)
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error getting signature help: ${err instanceof Error ? err.message : String(err)}`
          }
        ],
        isError: true
      };
    }
  });
}

// build/tools/lsp-diagnostics.js
import { z as z9 } from "zod";
function registerLspDiagnosticsTool(server, lspClient) {
  server.tool("shader_diagnostics", "Get diagnostics (errors, warnings) for a shader file from the language server. Note: shader-ls diagnostics support is limited in current versions. For full compilation diagnostics, use compile_shader instead.", {
    shaderPath: z9.string().describe("Path to the shader file (e.g., Assets/Shaders/Character.shader)"),
    content: z9.string().optional().describe("Optional: shader source code content. If not provided, the file will be read from disk.")
  }, async ({ shaderPath, content }) => {
    try {
      await lspClient.ensureRunning();
      return {
        content: [
          {
            type: "text",
            text: "shader-ls does not yet support pull-based diagnostics.\nThis feature will be available in a future version of shader-ls.\n\nAlternative: Use the `compile_shader` tool for Unity-based compilation diagnostics."
          }
        ]
      };
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: `Error: ${err instanceof Error ? err.message : String(err)}`
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
    version: "0.1.2"
  });
  const bridge = new UnityBridge("ws://localhost:8090");
  const lspClient = new ShaderLspClient();
  registerShaderCompileTool(server, bridge);
  registerShaderAnalyzeTools(server, bridge);
  registerShaderVariantsTools(server, bridge);
  registerShaderPropertiesTools(server, bridge);
  registerMaterialInfoTools(server, bridge);
  registerLspHoverTool(server, lspClient);
  registerLspCompletionTool(server, lspClient);
  registerLspSignatureTool(server, lspClient);
  registerLspDiagnosticsTool(server, lspClient);
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
