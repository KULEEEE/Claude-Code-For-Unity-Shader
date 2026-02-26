import { spawn } from "child_process";

interface AIRequest {
  prompt: string;
  shaderContext?: string;
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
      // and special character escaping issues
      const proc = spawn("claude", ["-p"], {
        timeout: 120000, // 120 second timeout
        env: { ...process.env },
        shell: true,
        stdio: ["pipe", "pipe", "pipe"],
      });

      let stdout = "";
      let stderr = "";

      proc.stdout.on("data", (data: Buffer) => {
        stdout += data.toString();
      });

      proc.stderr.on("data", (data: Buffer) => {
        stderr += data.toString();
      });

      proc.on("close", (code: number | null) => {
        if (code === 0) {
          resolve({ success: true, response: stdout.trim() });
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
