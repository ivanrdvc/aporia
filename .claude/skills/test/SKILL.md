---
name: test
description: "Run Revu tests: /test, /test session, /test run, /test cleanup"
user-invocable: true
disable-model-invocation: true
argument-hint: "[session | run | cleanup]"
---

## Argument routing

| Command | What it does |
|---|---|
| `/test` | Run `ReviewTests` against real ADO + LLM, then analyze the session |
| `/test session [session-dir]` | Analyze latest (or specified) session — tool use performance report |
| `/test run` | Create branch + PR in test repo, start server, fire webhook, check results |
| `/test cleanup` | Delete all comments on the active test PR |

---

## Context budget rules

This skill is designed for repeated runs. **Minimize what enters Claude Code context:**

- Always pipe test output to a temp file — never let raw `dotnet test` stdout into context.
- Only read the last ~15 lines (pass/fail + test output) from the log file.
- Use the verify script for analysis — its output is compact (~40 lines).
- Never read session JSON files directly into context. The verify script does that externally.

---

## Test repo

Configured in `tests/Revu.Tests.Integration/appsettings.test.json` under `TestRepo`.
Set your provider, project, repo ID, and repo name there.

## Secrets

All in **user-secrets** (see `UserSecretsId` in `Revu.csproj`):
`AI:OpenAI:ApiKey`, `AzureDevOps:PersonalAccessToken`, `Cosmos:ConnectionString`.

---

## `/test`

Run the review pipeline against a real PR. The target PR is configured in
`tests/Revu.Tests.Integration/appsettings.test.json` under `TestTarget`:

```json
"TestTarget": {
  "PrId": 12,
  "Branch": "refs/heads/feature/order-tracking-notifications"
}
```

Change `PrId` and `Branch` to target a different PR.

### Procedure — follow exactly, no improvisation

Run these three commands in order. This is the complete procedure whether the test passes or fails.

**Step 1** — run the test:
```bash
dotnet test tests/Revu.Tests.Integration/Revu.Tests.Integration.csproj \
  --filter "Review_FullPipeline_PostsFindings" \
  --logger "console;verbosity=detailed" > /tmp/revu-test.log 2>&1
```

**Step 2** — show the tail:
```bash
tail -15 /tmp/revu-test.log
```

**Step 3** — run verify:
```bash
python3 .claude/skills/test/scripts/verify.py --log /tmp/revu-test.log
```

Add `--otel` to pull the log timeline from OpenObserve (requires Docker running):
```bash
python3 .claude/skills/test/scripts/verify.py --log /tmp/revu-test.log --otel
```

The `--otel` section appends a timestamped log timeline for each trace — exploration
dispatches, completions, warnings, and errors — filtered for signal (HTTP retry noise
suppressed). Useful when diagnosing failures or unexpected behavior. OpenObserve must be
running locally (`docker compose up`); if unavailable the section is skipped gracefully.

Print the verify output to the user — that IS the full report. Do not add your own summary
afterwards. The verify output contains everything: findings, recall, token usage, model name,
tool calls, wall clock, session count, verdict, and parse failure detection. Repeating a subset
of this loses information. **Done. Stop here.**

On failure, the verify script extracts what it can from a partial run. If the user asks you to
investigate further, then dig in.

### Verify output structure

The verify script produces these sections in order:

```
============================================================
  Session analysis: run-YYYYMMDD-HHMMSS
  Sessions: <N>
  Path: <session directory>
============================================================

## Summary

  Findings:  <found> / <cap>  [! hit cap]     ← how many findings vs maxComments
  Reviewer:  <N> calls, <N> LLM turns, <N> explores
  Total:     <N> tool calls  |  {tool: count}  ← aggregate tool use across all agents
  Verdict:   CLEAN | <issues>                  ← tool use problems (dupes, 404s, empty searches)

## Reviewer                                     ← per-session-file breakdown

  --- 01.json ---
    Tool calls: <N>  |  LLM turns: <N>
    Tools: {tool: count}
    Workflow: Optimal | <issues>

  --- 02.json ---  (explorers, if any)
    ...

## Token Usage                                  ← per-agent, per-model breakdown

  trace: <trace-id>
    Reviewer       (<model>):  <in> in / <out> out  (<N> calls, peak: <N> in)
    Explorer       (<model>):  <in> in / <out> out  (<N> calls, peak: <N> in)
    Total                      <in> in / <out> out  (<N> calls)

## Finding Coverage                             ← eval against expected findings

  Recall: <hit>/<total> (<pct>%)  [required: n/n, optional: n/n]

  Required:
    [+] <label>  (<file>)                       ← matched
    [MISS] <label>  (<file>)                    ← not found — regression signal

  Optional:
    [+] <label>  (<file>)
    [-] <label>  (<file>)                       ← not found — acceptable

  Extra findings (<N>):                         ← findings not in expected set
    ? <file> — <truncated description>
```

**Key signals to watch:**
- `Verdict: CLEAN` = no tool use issues. Anything else = investigate.
- `[MISS]` on a required finding = regression — the reviewer should have caught this.
- `! hit cap` = findings were capped; there may be more the reviewer wanted to report.
- `Recall` dropping between runs = prompt or strategy regression.
- `0 tool calls` = reviewer answered from diff alone (fast but may miss cross-file issues).
- `Extra findings` = findings not in the eval set. Could be valid or noise.

### Verbose variant

For verbose output (full threads + findings), use `--filter "Verbose"` instead. Still pipe to
file and tail. Same three-step procedure.

### Scenarios

Named scenarios are in `Fixtures/Scenarios.cs` — that's the single source of truth for PR IDs
and branches.

---

## `/test session`

Analyze the latest session run (or a specific one) for tool use performance:

```bash
python3 .claude/skills/test/scripts/verify.py [session-dir] [--log /tmp/revu-test.log]
```

Print the full script output to the user.

---

## Sessions

Integration tests capture every agent invocation as numbered JSON files via `FileSessionProvider`.

### Location

```
tests/Revu.Tests.Integration/bin/Debug/net10.0/sessions/run-{yyyyMMdd-HHmmss}/
```

Each run directory contains `01.json`, `02.json`, etc. — one per agent invocation, in order.
Explorers finish first. The reviewer is always the last file — if missing, the run didn't complete
(sessions are written on completion only).

### Envelope format

```json
{
  "agent": "Reviewer" | "Explorer",
  "instructions": "<system prompt>",
  "tools": [{ "name": "...", "description": "...", "schema": { ... } }],
  "messages": [<ChatMessage array>]
}
```

### Message content types

`Contents[]` items, discriminated by `$type`:
- `"text"` — `{ "$type": "text", "Text": "..." }`
- `"functionCall"` — `{ "$type": "functionCall", "CallId": "...", "Name": "FetchFile", "Arguments": { "paths": [...] } }`
- `"functionResult"` — `{ "$type": "functionResult", "CallId": "...", "Result": "..." }`

### What to look for

- **Tool call sequence**: SearchCode → FetchFile (good) vs guessing paths (bad)
- **File-not-found**: Check `functionResult` for `"File not found"`
- **Duplicate calls**: Same file fetched or same query searched twice
- **Empty searches**: SearchCode returning "No results" — wasted tool calls
- **Explore queries**: Were they well-scoped?
- **Explorer efficiency**: Few tool calls or thrashing?

---

## `/test run`

End-to-end: create a test PR, start the server, fire the webhook, check results.

### 1. Create branch + PR

Ask the user for a branch name, or generate one (e.g. `test/run-YYYYMMDD`).

```bash
cd <test-repo-path>
git checkout main && git pull
git checkout -b <branch-name>
```

Make changes (ask user or generate a test file), commit, push:
```bash
git add . && git commit -m "test changes" && git push -u origin <branch-name>
```

Create PR using the appropriate CLI for your provider. Examples:

**ADO:**
```bash
az repos pr create \
  --org https://dev.azure.com/<org> \
  --project <project> \
  --repository <repo> \
  --source-branch <branch-name> \
  --target-branch main \
  --title "Test: <branch-name>"
```

**GitHub:**
```bash
gh pr create --title "Test: <branch-name>" --body "test run"
```

Note the `pullRequestId`.

### 2. Start server

```bash
dotnet run --project Revu/Revu.csproj
```

Listens on `http://localhost:5018`.

### 3. Fire webhook

**Important:** If a webhook subscription is active, the provider will auto-fire — skip the manual curl.

Read the `TestRepo` section from `appsettings.test.json` for the repo coordinates, then POST
a webhook payload to the appropriate provider endpoint. Example for ADO:

```bash
curl -s -X POST http://localhost:5018/webhook/ado \
  -H "Content-Type: application/json" \
  -d '{
  "eventType": "git.pullrequest.created",
  "resource": {
    "repository": {
      "id": "<repo-id>",
      "name": "<repo-name>",
      "project": {
        "id": "<project-id>",
        "name": "<project-name>"
      }
    },
    "pullRequestId": <PRID>,
    "status": "active",
    "sourceRefName": "refs/heads/<branch-name>",
    "targetRefName": "refs/heads/main",
    "isDraft": false
  }
}'
```

### 4. Check results

Query PR threads using the appropriate CLI for your provider. Example for ADO:

```bash
az devops invoke \
  --org https://dev.azure.com/<org> \
  --area git \
  --resource pullRequestThreads \
  --route-parameters project=<project> repositoryId=<repo-id> pullRequestId=<PRID> \
  --api-version 7.1
```

### 5. Clean up

Stop the background server when done.

---

## `/test cleanup`

```bash
dotnet test tests/Revu.Tests.Integration/Revu.Tests.Integration.csproj \
  --filter "DeleteAllComments" \
  --logger "console;verbosity=detailed"
```
