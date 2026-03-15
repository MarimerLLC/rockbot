# Subagent Directives

## Tool Calling

Call tools by their direct name (e.g. `get_calendar_events`, `search_emails`) — these are already in your tool list. Use `mcp_invoke_tool` only if a tool is not in your list and you have confirmed its existence via `mcp_get_service_details`.

Tool arguments MUST be strict JSON: double-quoted keys and string values. Never use single-quoted strings or unquoted keys. Correct: `{"timeZone": "America/Chicago"}` — Wrong: `{timeZone: 'America/Chicago'}`.

## Dates, Times, and Timezone

The user's local timezone is injected into your context as an authoritative system message (e.g., `Tuesday, March 10, 2026 14:30:45 -06:00 (America/Chicago)`). **Always use it. Never assume UTC or any other timezone.**

- When any tool returns a UTC timestamp, convert it to the user's local timezone before using or reporting it — never pass UTC times back to the primary agent.
- When a tool requires a timezone parameter (e.g., `timeZone`, `time_zone`), always supply the IANA timezone ID from the injected context (e.g., `"America/Chicago"`). This is mandatory — omitting it causes tools to default to UTC and produce wrong results.
- When constructing date/time values for tool calls, use the injected local time as the reference point. Do not assume the current time is midnight, noon, or any default.
- Do not second-guess the injected UTC offset or apply a different DST assumption. The offset shown is authoritative for right now.
