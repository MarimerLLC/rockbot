You are analyzing wisp pipeline executions to identify successful patterns worth promoting to skill resources.

A wisp is a lightweight multi-step pipeline. Each candidate group below shares the same `definitionHash` — the steps are identical across runs — and has succeeded repeatedly with no failures, indicating a reusable pattern.

Each candidate carries:
- `definitionHash` — content hash of the step definitions
- `frequency` — total successful runs in the window
- `distinctSessions` — number of distinct sessions that ran it
- `description` — the wisp definition's own description
- `invokingSkill` — the skill that was active in the originating session, when one could be resolved
- `bodyPreview` — first ~1 KiB of the JSON definition (truncated for prompt budget)

For each candidate worth promoting, emit a `Promotion` entry. Skip candidates where:
- `invokingSkill` is null or not in the existing-skills list (no target skill to attach to)
- the description is too generic to suggest a useful resource name (e.g. "test", "scratch")
- the candidate looks like a one-off rather than a reusable pattern

Respond with a JSON object:

```json
{
  "promotions": [
    {
      "targetSkill": "calendar/mcp-calendar-operations",
      "filename": "scan-events-fan-out.json",
      "resourceType": "Wisp",
      "description": "Per-account get_calendar_events fan-out with timeZone and accountId",
      "definitionHash": "abc123…"
    }
  ]
}
```

Filename rules:
- lowercase, hyphens for spaces, single dot for the extension
- extension matches `resourceType` (`.json` for `Wisp` and `JsonSchema`, `.py` for `Python`, `.md` for `Markdown`, `.txt` otherwise)
- short and descriptive — what the asset does, not what data it operates on

Filter zero false positives over recall. When in doubt, omit the candidate. Empty `promotions` array is a fine answer.
