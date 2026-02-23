# Heartbeat Patrol Directive

You are executing a periodic heartbeat patrol. This fires once per hour.

## CRITICAL: Output Rule

**Your response to this message must contain zero text.** No summary, no status update, no confirmation that you ran. Nothing.

All findings go to memory. The user receives a briefing when they next connect via session-start. Any text you produce here goes nowhere useful and wastes tokens.

If you have nothing to write to memory: return an empty response immediately.

---

## Before You Begin

Load the proactive actions skill if it exists:

```
get_skill("patrol/proactive-actions")
```

If it exists, execute any checks listed there in addition to the base checklist below.

## Patrol Checklist

Work through each item silently. Write findings to memory. Do not narrate.

1. **Active plans** — scan `plans/` memory for unblocked plans ready to advance, or plans stalled 3+ days. Write a `briefing-queue/YYYY-MM-DD` entry if found.

2. **Calendar (next 4 hours)** — check for meetings starting within 60 minutes with no prep notes, conflicts, or items needing action. Write a `briefing-queue/YYYY-MM-DD` entry if found.

3. **Email triage** — scan email from the last 2 hours. Look for messages requiring a response within 24 hours, or anything urgent. Write a `briefing-queue/YYYY-MM-DD` entry if found. For genuine time-critical items, send an email to self with subject `[URGENT] ...`.

4. **Scheduled task health** — list all scheduled tasks. Flag any overdue, erroring, or unexpectedly unfired. Write a `briefing-queue/YYYY-MM-DD` entry if found.

5. **Pending tasks and work queues** — call `mcp_list_services` and check any connected server that surfaces tasks or pending work items (currently: `todo-mcp`). Check for items overdue or due within 24 hours. Write a `briefing-queue/YYYY-MM-DD` entry if found.

## Writing Briefing Entries

Before writing any briefing entry:
- Check `briefing-queue/YYYY-MM-DD` to confirm this item is not already queued.
- Keep entries concise: one or two sentences per finding.
- Group related findings into a single entry rather than one entry per item.

## After the Patrol

1. Write a brief `patrol-log` entry to working memory: timestamp, items found, actions taken. This is internal bookkeeping — not for the user.

2. If you observed a new recurring pattern worth checking on future patrols, update `patrol/proactive-actions` via `save_skill`. Only add patterns that recur, not one-offs.

3. **Produce no text output. End the response.**
