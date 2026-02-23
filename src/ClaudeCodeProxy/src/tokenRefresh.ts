import { execFile } from "child_process";
import { promisify } from "util";

const execFileAsync = promisify(execFile);

const REFRESH_INTERVAL_MS = 23 * 60 * 60 * 1000; // 23 hours

/**
 * Runs `claude --version` to trigger the CLI's built-in OAuth token refresh.
 *
 * The Claude Code CLI checks token expiry on every invocation. If the access
 * token has expired it uses the refresh token to obtain a new one and writes
 * the updated credentials back to ~/.claude/credentials.json.  Our auth.ts
 * does a fresh fs.readFileSync on every request, so the proxy picks up the
 * renewed token automatically with no restart needed.
 *
 * Preconditions:
 *   - `claude` must be on PATH (installed in the Docker image)
 *   - ~/.claude/ must be on a writable PVC (not a read-only k8s Secret mount)
 */
export async function refreshToken(): Promise<void> {
  try {
    const { stdout } = await execFileAsync("claude", ["--version"], {
      timeout: 30_000,
      env: { ...process.env, HOME: process.env.HOME ?? "/root" },
    });
    console.log(`[token-refresh] claude --version: ${stdout.trim()}`);
  } catch (err) {
    // Non-fatal — the current token may still be valid.
    console.warn(`[token-refresh] claude --version failed: ${(err as Error).message}`);
  }
}

/**
 * Runs an immediate refresh on startup, then schedules one every 23 hours.
 * Only active when credentials.json auth is in use (OAuth path).
 */
export function startTokenRefreshScheduler(): void {
  // Skip if using an API key — no OAuth refresh needed.
  if (process.env.ANTHROPIC_API_KEY || process.env.CLAUDE_ACCESS_TOKEN) {
    console.log("[token-refresh] API key auth detected — scheduler disabled");
    return;
  }

  console.log("[token-refresh] Starting OAuth token refresh scheduler (every 23h)");

  // Immediate refresh on startup so we know the token is valid before
  // accepting any traffic.
  refreshToken();

  setInterval(refreshToken, REFRESH_INTERVAL_MS);
}
