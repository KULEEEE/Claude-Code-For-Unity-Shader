import { spawn, execSync, type ChildProcess } from "child_process";
import { pathToFileURL } from "url";
import { readFileSync } from "fs";
import { resolve } from "path";
import {
  createProtocolConnection,
  StreamMessageReader,
  StreamMessageWriter,
  InitializeRequest,
  InitializedNotification,
  DidOpenTextDocumentNotification,
  DidChangeTextDocumentNotification,
  HoverRequest,
  CompletionRequest,
  SignatureHelpRequest,
  type InitializeParams,
  type ProtocolConnection,
  type Hover,
  type CompletionList,
  type CompletionItem,
  type SignatureHelp,
  TextDocumentSyncKind,
} from "vscode-languageserver-protocol/node.js";

const LSP_INIT_TIMEOUT = 15000;
const LSP_REQUEST_TIMEOUT = 10000;
const SHADER_LS_COMMAND = "shader-ls";

interface OpenDocument {
  uri: string;
  version: number;
  content: string;
}

/**
 * LSP client that wraps shader-language-server (shader-ls) as a child process.
 * Features: lazy initialization, auto-install via dotnet, crash recovery.
 */
export class ShaderLspClient {
  private process: ChildProcess | null = null;
  private connection: ProtocolConnection | null = null;
  private openDocuments = new Map<string, OpenDocument>();
  private initPromise: Promise<void> | null = null;
  private isShuttingDown = false;
  private projectRoot: string;

  constructor(projectRoot?: string) {
    this.projectRoot = projectRoot ?? process.cwd();
  }

  /**
   * Ensure the LSP server is running. Called lazily on first tool use.
   */
  async ensureRunning(): Promise<void> {
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

  private async startServer(): Promise<void> {
    // Try to spawn shader-ls
    let spawned = this.trySpawn();

    if (!spawned) {
      // Attempt auto-install
      await this.autoInstall();
      spawned = this.trySpawn();
      if (!spawned) {
        throw new Error(
          "Failed to start shader-ls after installation. " +
            "Please verify the installation with: dotnet tool list -g"
        );
      }
    }

    await this.initializeLsp();
  }

  private trySpawn(): boolean {
    try {
      const isWindows = process.platform === "win32";
      const command = isWindows ? `${SHADER_LS_COMMAND}.exe` : SHADER_LS_COMMAND;

      this.process = spawn(command, ["--stdio"], {
        stdio: ["pipe", "pipe", "pipe"],
        env: { ...process.env, DOTNET_ROLL_FORWARD: "LatestMajor" },
        shell: isWindows,
      });

      // Check if process started successfully
      if (!this.process.pid) {
        this.process = null;
        return false;
      }

      this.process.on("exit", (code) => {
        if (!this.isShuttingDown) {
          console.error(
            `[ShaderMCP-LSP] shader-ls exited with code ${code}. Will restart on next request.`
          );
        }
        this.cleanup();
      });

      this.process.on("error", (err) => {
        console.error(`[ShaderMCP-LSP] shader-ls process error: ${err.message}`);
        this.cleanup();
      });

      this.process.stderr?.on("data", (data: Buffer) => {
        console.error(`[shader-ls] ${data.toString().trim()}`);
      });

      return true;
    } catch {
      this.process = null;
      return false;
    }
  }

  private async autoInstall(): Promise<void> {
    console.error("[ShaderMCP-LSP] shader-ls not found. Attempting auto-install...");

    // Check if dotnet is available
    const dotnetCmd = process.platform === "win32" ? "where dotnet" : "which dotnet";
    try {
      execSync(dotnetCmd, { stdio: "pipe" });
    } catch {
      throw new Error(
        "shader-ls is not installed and .NET SDK was not found.\n" +
          "To use LSP features, please:\n" +
          "1. Install .NET 7.0+ SDK from https://dotnet.microsoft.com/download\n" +
          "2. Run: dotnet tool install --global shader-ls\n\n" +
          "Note: Existing shader MCP tools (compile, analyze, etc.) work without this."
      );
    }

    // Install shader-ls globally
    console.error("[ShaderMCP-LSP] Installing shader-ls via dotnet...");
    try {
      execSync("dotnet tool install --global shader-ls", {
        stdio: "pipe",
        timeout: 120000,
      });
      console.error("[ShaderMCP-LSP] shader-ls installed successfully.");
    } catch (err) {
      // It might already be installed — try updating
      try {
        execSync("dotnet tool update --global shader-ls", {
          stdio: "pipe",
          timeout: 120000,
        });
        console.error("[ShaderMCP-LSP] shader-ls updated successfully.");
      } catch (updateErr) {
        throw new Error(
          `Failed to install shader-ls: ${err instanceof Error ? err.message : String(err)}\n` +
            "Please install manually: dotnet tool install --global shader-ls"
        );
      }
    }
  }

  private async initializeLsp(): Promise<void> {
    if (!this.process?.stdout || !this.process?.stdin) {
      throw new Error("shader-ls process has no stdio streams");
    }

    const reader = new StreamMessageReader(this.process.stdout);
    const writer = new StreamMessageWriter(this.process.stdin);
    this.connection = createProtocolConnection(reader, writer);

    this.connection.listen();

    const initParams: InitializeParams = {
      processId: process.pid,
      capabilities: {
        textDocument: {
          hover: { contentFormat: ["markdown", "plaintext"] },
          completion: {
            completionItem: {
              snippetSupport: false,
              documentationFormat: ["markdown", "plaintext"],
            },
          },
          signatureHelp: {
            signatureInformation: {
              documentationFormat: ["markdown", "plaintext"],
            },
          },
        },
      },
      rootUri: pathToFileURL(this.projectRoot).toString(),
      workspaceFolders: [
        {
          uri: pathToFileURL(this.projectRoot).toString(),
          name: "workspace",
        },
      ],
    };

    const initResult = await Promise.race([
      this.connection.sendRequest(InitializeRequest.type, initParams),
      new Promise<never>((_, reject) =>
        setTimeout(
          () => reject(new Error("LSP initialization timed out")),
          LSP_INIT_TIMEOUT
        )
      ),
    ]);

    console.error(
      `[ShaderMCP-LSP] LSP initialized. Server: ${initResult.serverInfo?.name ?? "unknown"} ${initResult.serverInfo?.version ?? ""}`
    );

    await this.connection.sendNotification(InitializedNotification.type, {});
  }

  /**
   * Convert a Unity asset path to a file:// URI.
   */
  private toUri(shaderPath: string): string {
    const absPath = resolve(this.projectRoot, shaderPath);
    return pathToFileURL(absPath).toString();
  }

  /**
   * Open or update a document in the LSP server.
   */
  private async openOrUpdateDocument(
    shaderPath: string,
    content?: string
  ): Promise<string> {
    await this.ensureRunning();

    const uri = this.toUri(shaderPath);
    const languageId = shaderPath.endsWith(".hlsl") ? "hlsl" : "shaderlab";

    // Read file content if not provided
    const docContent =
      content ?? readFileSync(resolve(this.projectRoot, shaderPath), "utf-8");

    const existing = this.openDocuments.get(uri);

    if (!existing) {
      // Open new document
      await this.connection!.sendNotification(
        DidOpenTextDocumentNotification.type,
        {
          textDocument: {
            uri,
            languageId,
            version: 1,
            text: docContent,
          },
        }
      );

      this.openDocuments.set(uri, { uri, version: 1, content: docContent });
    } else if (existing.content !== docContent) {
      // Update existing document
      const newVersion = existing.version + 1;
      await this.connection!.sendNotification(
        DidChangeTextDocumentNotification.type,
        {
          textDocument: { uri, version: newVersion },
          contentChanges: [{ text: docContent }],
        }
      );

      this.openDocuments.set(uri, {
        uri,
        version: newVersion,
        content: docContent,
      });
    }

    return uri;
  }

  /**
   * Get hover information at a position.
   */
  async hover(
    shaderPath: string,
    line: number,
    character: number,
    content?: string
  ): Promise<Hover | null> {
    const uri = await this.openOrUpdateDocument(shaderPath, content);

    return Promise.race([
      this.connection!.sendRequest(HoverRequest.type, {
        textDocument: { uri },
        position: { line, character },
      }),
      new Promise<null>((resolve) =>
        setTimeout(() => resolve(null), LSP_REQUEST_TIMEOUT)
      ),
    ]);
  }

  /**
   * Get completion suggestions at a position.
   */
  async completion(
    shaderPath: string,
    line: number,
    character: number,
    content?: string
  ): Promise<CompletionItem[] | CompletionList | null> {
    const uri = await this.openOrUpdateDocument(shaderPath, content);

    return Promise.race([
      this.connection!.sendRequest(CompletionRequest.type, {
        textDocument: { uri },
        position: { line, character },
      }),
      new Promise<null>((resolve) =>
        setTimeout(() => resolve(null), LSP_REQUEST_TIMEOUT)
      ),
    ]);
  }

  /**
   * Get signature help at a position.
   */
  async signatureHelp(
    shaderPath: string,
    line: number,
    character: number,
    content?: string
  ): Promise<SignatureHelp | null> {
    const uri = await this.openOrUpdateDocument(shaderPath, content);

    return Promise.race([
      this.connection!.sendRequest(SignatureHelpRequest.type, {
        textDocument: { uri },
        position: { line, character },
      }),
      new Promise<null>((resolve) =>
        setTimeout(() => resolve(null), LSP_REQUEST_TIMEOUT)
      ),
    ]);
  }

  /**
   * Gracefully shut down the LSP server.
   */
  async shutdown(): Promise<void> {
    this.isShuttingDown = true;

    if (this.connection) {
      try {
        await Promise.race([
          this.connection.sendRequest("shutdown"),
          new Promise<void>((resolve) => setTimeout(resolve, 3000)),
        ]);
        this.connection.sendNotification("exit");
      } catch {
        // Ignore errors during shutdown
      }
    }

    this.cleanup();
  }

  private cleanup(): void {
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
}
