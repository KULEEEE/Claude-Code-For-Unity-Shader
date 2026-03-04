import { spawn } from "child_process";
import { tmpdir } from "os";

interface AIRequest {
  prompt: string;
  shaderContext?: string;
  onChunk?: (chunk: string) => void;
}

interface AIResponse {
  success: boolean;
  response?: string;
  error?: string;
}

/**
 * Handle an AI query by calling the Claude CLI (`claude -p`).
 * Uses the existing Claude Code authentication — no API key needed.
 * Prompt is passed via stdin to avoid command-line length limits on Windows.
 */
export async function handleAIQuery(request: AIRequest): Promise<AIResponse> {
  const fullPrompt = buildFullPrompt(request.prompt, request.shaderContext);

  return new Promise((resolve) => {
    try {
      // Use stdin pipe to pass prompt — avoids Windows cmd length limits
      // and special character escaping issues.
      // cwd set to temp dir to prevent claude from loading project .mcp.json,
      // which would spawn a competing MCP server and break the WebSocket connection.
      // --output-format stream-json enables token-by-token streaming via stdout.
      const proc = spawn("claude", ["-p", "--output-format", "stream-json"], {
        timeout: 120000, // 120 second timeout
        env: { ...process.env },
        shell: true,
        stdio: ["pipe", "pipe", "pipe"],
        cwd: tmpdir(),
      });

      let fullResult = "";
      let stderr = "";
      let lineBuffer = "";

      proc.stdout.on("data", (data: Buffer) => {
        lineBuffer += data.toString();
        const lines = lineBuffer.split("\n");
        lineBuffer = lines.pop() || ""; // keep incomplete last line

        for (const line of lines) {
          if (!line.trim()) continue;
          try {
            const event = JSON.parse(line);
            if (
              event.type === "content_block_delta" &&
              event.delta?.type === "text_delta"
            ) {
              request.onChunk?.(event.delta.text);
            } else if (event.type === "result") {
              fullResult = event.result || "";
            }
          } catch {
            // partial or non-JSON line, skip
          }
        }
      });

      proc.stderr.on("data", (data: Buffer) => {
        stderr += data.toString();
      });

      proc.on("close", (code: number | null) => {
        // Process any remaining buffered line
        if (lineBuffer.trim()) {
          try {
            const event = JSON.parse(lineBuffer);
            if (event.type === "result") {
              fullResult = event.result || "";
            }
          } catch {}
        }

        if (code === 0) {
          resolve({ success: true, response: fullResult });
        } else {
          console.error(
            `[AI Handler] Claude exited with code ${code}: ${stderr}`
          );
          resolve({
            success: false,
            error: `Claude exited with code ${code}. ${stderr.substring(0, 200)}`,
          });
        }
      });

      proc.on("error", (err: Error) => {
        console.error(`[AI Handler] Failed to spawn claude: ${err.message}`);
        resolve({
          success: false,
          error: `Failed to run Claude CLI: ${err.message}. Ensure 'claude' is in PATH.`,
        });
      });

      // Write prompt to stdin and close
      proc.stdin.write(fullPrompt);
      proc.stdin.end();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      resolve({ success: false, error: `Exception: ${msg}` });
    }
  });
}

function buildFullPrompt(
  userPrompt: string,
  shaderContext?: string
): string {
  let prompt =
    "You are a Unity shader expert assistant. " +
    "Answer clearly and concisely. Use the user's language when possible. " +
    "When suggesting code changes, provide specific code snippets.\n\n";

  if (shaderContext) {
    prompt += `Shader Context:\n${shaderContext}\n\n`;
  }

  prompt += `User Question:\n${userPrompt}`;

  return prompt;
}
