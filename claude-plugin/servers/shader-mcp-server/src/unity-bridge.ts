import WebSocket from "ws";
import { randomUUID } from "crypto";

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * WebSocket client that connects to Unity Editor's ShaderMCP server.
 * Features: auto-reconnect, UUID-based request/response matching, timeouts.
 */
export class UnityBridge {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<string, PendingRequest>();
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private isConnecting = false;
  private _isConnected = false;

  private readonly url: string;
  private readonly maxReconnectAttempts: number;
  private readonly reconnectInterval: number;
  private readonly defaultTimeout: number;

  constructor(
    url = "ws://localhost:8090",
    options?: {
      maxReconnectAttempts?: number;
      reconnectInterval?: number;
      defaultTimeout?: number;
    }
  ) {
    this.url = url;
    this.maxReconnectAttempts = options?.maxReconnectAttempts ?? 10;
    this.reconnectInterval = options?.reconnectInterval ?? 3000;
    this.defaultTimeout = options?.defaultTimeout ?? 10000;
  }

  get isConnected(): boolean {
    return this._isConnected && this.ws?.readyState === WebSocket.OPEN;
  }

  /**
   * Connect to Unity WebSocket server (non-blocking).
   */
  async connect(): Promise<void> {
    if (this.isConnecting || this.isConnected) return;
    this.isConnecting = true;

    return new Promise<void>((resolve) => {
      try {
        this.ws = new WebSocket(this.url);

        this.ws.on("open", () => {
          this._isConnected = true;
          this.isConnecting = false;
          this.reconnectAttempts = 0;
          console.error("[ShaderMCP] Connected to Unity");
          resolve();
        });

        this.ws.on("message", (data: WebSocket.RawData) => {
          this.handleMessage(data.toString());
        });

        this.ws.on("close", () => {
          this._isConnected = false;
          this.isConnecting = false;
          console.error("[ShaderMCP] Disconnected from Unity");
          this.scheduleReconnect();
        });

        this.ws.on("error", (err: Error) => {
          this.isConnecting = false;
          console.error(`[ShaderMCP] WebSocket error: ${err.message}`);
          // Don't reject — just schedule reconnect
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
  async request(
    method: string,
    params: Record<string, unknown> = {},
    timeoutMs?: number
  ): Promise<unknown> {
    if (!this.isConnected) {
      throw new Error(
        "Not connected to Unity. Please ensure the Shader MCP Server is running in Unity Editor (Tools > Shader MCP > Server Window)."
      );
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
      this.ws!.send(message);
    });
  }

  /**
   * Disconnect from Unity.
   */
  disconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }

    this._isConnected = false;

    // Reject all pending requests
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.reject(new Error("Disconnected"));
    }
    this.pendingRequests.clear();
  }

  private handleMessage(raw: string): void {
    try {
      const msg = JSON.parse(raw);
      const id = msg.id;

      if (!id || !this.pendingRequests.has(id)) {
        console.error(`[ShaderMCP] Received message with unknown id: ${id}`);
        return;
      }

      const pending = this.pendingRequests.get(id)!;
      this.pendingRequests.delete(id);
      clearTimeout(pending.timer);

      if (msg.error) {
        pending.reject(
          new Error(`Unity error (${msg.error.code}): ${msg.error.message}`)
        );
      } else {
        pending.resolve(msg.result);
      }
    } catch (err) {
      console.error(`[ShaderMCP] Failed to parse message: ${err}`);
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.maxReconnectAttempts) {
      console.error("[ShaderMCP] Max reconnect attempts reached");
      return;
    }

    if (this.reconnectTimer) return;

    this.reconnectAttempts++;
    console.error(
      `[ShaderMCP] Reconnecting in ${this.reconnectInterval}ms (attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts})`
    );

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect().catch(() => {});
    }, this.reconnectInterval);
  }
}
