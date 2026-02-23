import fs from "fs";
import path from "path";

export interface AuthResult {
  header: string;
  value: string;
  source: string;
}

/**
 * Resolves Anthropic API credentials in priority order:
 *
 * 1. ANTHROPIC_API_KEY env var — standard Console API key (pay-per-token)
 * 2. CLAUDE_ACCESS_TOKEN env var — explicit OAuth token override
 * 3. ~/.claude/credentials.json — OAuth token from `claude auth login`
 *    (uses Max/Pro subscription; personal-use allowed per Anthropic ToS clarification Feb 2026)
 *
 * The Claude Code OAuth token is stored in credentials.json and is used
 * by the claude CLI.  For personal autonomous agents Anthropic explicitly
 * permits using this token directly with the Anthropic Messages API.
 */
export function resolveAuth(): AuthResult {
  // 1. Explicit API key — highest priority, standard pay-per-token billing
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (apiKey) {
    return { header: "x-api-key", value: apiKey, source: "ANTHROPIC_API_KEY" };
  }

  // 2. Explicit OAuth token override
  const tokenOverride = process.env.CLAUDE_ACCESS_TOKEN;
  if (tokenOverride) {
    return {
      header: "Authorization",
      value: `Bearer ${tokenOverride}`,
      source: "CLAUDE_ACCESS_TOKEN",
    };
  }

  // 3. Claude Code credentials file (Max/Pro subscription)
  const credentialsPath =
    process.env.CLAUDE_CREDENTIALS_PATH ??
    path.join(process.env.HOME ?? "/root", ".claude", "credentials.json");

  if (fs.existsSync(credentialsPath)) {
    try {
      const raw = fs.readFileSync(credentialsPath, "utf-8");
      const creds = JSON.parse(raw);
      const token: string | undefined =
        creds?.claudeAiOauth?.accessToken ??
        creds?.access_token ??
        creds?.token;
      if (token) {
        return {
          header: "Authorization",
          value: `Bearer ${token}`,
          source: credentialsPath,
        };
      }
    } catch {
      // fall through
    }
  }

  throw new Error(
    "No Anthropic credentials found. Set ANTHROPIC_API_KEY, CLAUDE_ACCESS_TOKEN, " +
      `or run 'claude auth login' to create ${credentialsPath}.`
  );
}
