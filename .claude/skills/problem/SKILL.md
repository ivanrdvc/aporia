---
name: problem
description: "Capture a problem from the current conversation to revisit later: /problem"
user-invocable: true
disable-model-invocation: true
---

# Problem Capture Skill

You are capturing a problem from the current conversation so it can be solved later — with another AI, another session, or a different approach.

## Workflow

1. **Extract from context** — read the conversation and fill every field in `template.md` yourself. Derive the title, problem statement, error, tech context, what was tried, and status from what is already there. Do not ask the user for anything that can be inferred.
2. **Ask only for genuine gaps** — if a required checklist field cannot be reasonably inferred (e.g. no error message was ever shown, status is ambiguous), ask once for all missing items in a single message. Do not ask about optional fields.
3. **Slug** — generate a short kebab-case slug from the title (e.g. `azure-auth-token-expired`).
4. **Write** — save to `notes/problems/YYYY-MM-DD-<slug>.md` using `template.md`.
5. **Confirm** — print the file path and status. Nothing else.

## Rules

- Do NOT attempt to solve the problem. Capture only.
- Keep the problem statement factual and concise — it must be readable cold by another AI.
- If the user adds extra context verbally, include it under **Notes for Next Session**.

## Sessions

Integration test runs capture every agent invocation as numbered JSON files via
`FileSessionProvider`. Each file is one agent invocation — explorers finish first,
reviewer is always last. Sessions are written **on completion only** — a missing
reviewer file means the run failed before finishing.

**Location:**
```
tests/Revu.Tests.Integration/bin/Debug/net10.0/sessions/run-{yyyyMMdd-HHmmss}/
```

**Envelope format:**
```json
{
  "agent": "Reviewer" | "Explorer",
  "instructions": "<system prompt>",
  "tools": [{ "name": "...", "description": "...", "schema": {} }],
  "messages": [<ChatMessage array>],
  "error": "<exception message if the agent failed>"
}
```

**Message content types** (`contents[]`, discriminated by `$type`):
- `"text"` — `{ "$type": "text", "Text": "..." }`
- `"functionCall"` — `{ "$type": "functionCall", "Name": "FetchFile", "Arguments": { ... } }`
- `"functionResult"` — `{ "$type": "functionResult", "CallId": "...", "Result": "..." }`

**When to include a session path in the problem file:** only if a session directory is
already available and captures the bad behavior (e.g. wrong tool calls, a missing reviewer
file, an error in the envelope). Include the path under **Relevant Code / Files**. Do not
go looking for sessions — if they aren't already in context, skip this.

## OTEL Logs

If OpenObserve is running (`docker compose up`), the verify script can fetch a timestamped
log timeline for a trace — exploration dispatches, warnings, errors — filtered for signal:

```bash
python3 .claude/skills/test/scripts/verify.py --log /tmp/revu-test.log --otel
```

The timeline appears as `## OTEL Log Timeline` at the end of verify output. If a warning
or error in that timeline explains the problem (e.g. `Structured output returned empty
text`, agent crash), quote the relevant line in the problem file under **Observed
Behavior**. Only do this if verify output with `--otel` is already in context.
