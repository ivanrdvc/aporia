---
name: test
description: "Run Revu integration tests: /test [profile | session]"
user-invocable: true
disable-model-invocation: true
argument-hint: "[profile-hint | session]"
---

## Argument routing

| Command | What it does |
|---|---|
| `/test` | Use default profile (`TestProfile` in JSON), create fresh PR, run pipeline, verify, close PR |
| `/test <hint>` | Fuzzy-match a profile name from `TestProfiles`, then run. E.g. `/test ado`, `/test eshop`, `/test gh` |
| `/test session [session-dir]` | Analyze latest (or specified) session |

### Profile resolution

1. Read `appsettings.test.json` and get the keys from `TestProfiles`.
2. If no argument (bare `/test`), use the `TestProfile` value from JSON.
3. If argument given, find all profile names containing the hint (case-insensitive).
   - Exactly one match → use it.
   - Multiple matches → list them and ask the user which one.
   - Zero matches → tell the user no profile matched and list available ones.
4. The matched profile has `Provider` inside it — use that to pick the right procedure (GitHub or ADO).

---

## Context budget rules

This skill is designed for repeated runs. **Minimize what enters Claude Code context:**

- Always pipe test output to a temp file — never let raw `dotnet test` stdout into context.
- Only read the last ~15 lines (pass/fail + test output) from the log file.
- Use the verify script for analysis — its output is compact (~40 lines).
- Never read session JSON files directly into context. The verify script does that externally.

---

## Config file

`tests/Revu.Tests.Integration/appsettings.test.json` — profiles live under `TestProfiles`.
`TestProfile` is the default. `TEST_PROFILE` env var overrides it at runtime.

```json
"TestProfile": "ado-sc",
"TestProfiles": {
  "ado-sc": { "Provider": "Ado", "Organization": "ivanradovic", ... },
  "gh-eshop": { "Provider": "GitHub", "Organization": "ivanrdvc", ... }
}
```

## Secrets

All in **user-secrets** (see `UserSecretsId` in each `.csproj`):
- `AI:OpenAI:ApiKey`, `Cosmos:ConnectionString`
- ADO: `AzureDevOps:Organizations:<org>:PersonalAccessToken`
- GitHub: `GitHub:Organizations:<key>:Owner`, `GitHub:Organizations:<key>:Token`

## Source branches

These are the permanent branches with planted bugs. Each test run creates a temp copy.

| Provider | Branch | Description |
|---|---|---|
| **ADO** | `refs/heads/feature/order-tracking-notifications` | 15 files, planted bugs across 4 services |
| **GitHub** | `refs/heads/feature/order-tracking-notifications` | Same changes as ADO, on `ivanrdvc/eShop` fork |

---

## Procedure — follow exactly, no improvisation

### Step 0 — Resolve profile

Read `appsettings.test.json`. Follow the profile resolution rules above to determine the profile
name. Read `Provider`, `Organization`, `Project`, `RepositoryId`, `RepositoryName` from the
matched profile. Then follow the matching provider procedure below.

---

## GitHub procedure

### Step 1 — Create temp branch + PR

Use the profile's `Organization` and `RepositoryId` (format: `owner/repo`).

```bash
# Get SHA of source branch
sha=$(gh api repos/{owner}/{repo}/git/ref/heads/feature/order-tracking-notifications --jq '.object.sha')

# Create temp branch
branch="revu-test-$(date +%s)"
gh api repos/{owner}/{repo}/git/refs -f ref="refs/heads/$branch" -f sha="$sha"

# Create PR and capture number
pr_number=$(gh pr create --repo {owner}/{repo} --head "$branch" --base main \
  --title "revu-test-$branch" --body "Automated test PR" | grep -o '[0-9]*$')

echo "Created PR #$pr_number on branch $branch"
```

### Step 2 — Run the test

```bash
TEST_PROFILE={profile_name} TestTarget__PrId=$pr_number TestTarget__Branch="refs/heads/$branch" \
  dotnet test tests/Revu.Tests.Integration/Revu.Tests.Integration.csproj \
  --filter "Review_FullPipeline_PostsFindings" \
  --logger "console;verbosity=detailed" > /tmp/revu-test.log 2>&1
```

### Step 3 — Show the tail

```bash
tail -15 /tmp/revu-test.log
```

### Step 4 — Run verify

```bash
python3 .claude/skills/test/scripts/verify.py --log /tmp/revu-test.log
```

### Step 5 — Close PR + delete branch

```bash
gh pr close $pr_number --repo {owner}/{repo} --delete-branch
```

Print the verify output to the user — that IS the full report. Do not add your own summary
afterwards. **Done. Stop here.**

On failure, still close the PR (step 5), then show the verify output. If the user asks you to
investigate further, then dig in.

### Verbose variant

For verbose output (full threads + findings), use `--filter "Verbose"` instead in step 2.

---

## ADO procedure

### Step 1 — Create temp branch + PR

Use the profile's `Organization`, `Project`, and `RepositoryId`.

```bash
org="https://dev.azure.com/{organization}"
project="{project}"
repo_id="{repositoryId}"
source_branch="feature/order-tracking-notifications"

# Get SHA of source branch
sha=$(az repos ref list --repository "$repo_id" --org "$org" --project "$project" \
  --query "[?name=='refs/heads/$source_branch'].objectId" -o tsv)

# Create temp branch
branch="revu-test-$(date +%s)"
az repos ref create --name "refs/heads/$branch" --object-id "$sha" \
  --repository "$repo_id" --org "$org" --project "$project"

# Create PR and capture ID
pr_id=$(az repos pr create --repository "$repo_id" --source-branch "$branch" \
  --target-branch main --title "revu-test-$branch" \
  --org "$org" --project "$project" --query 'pullRequestId' -o tsv)

echo "Created PR #$pr_id on branch $branch"
```

### Step 2 — Run the test

```bash
TEST_PROFILE={profile_name} TestTarget__PrId=$pr_id TestTarget__Branch="refs/heads/$branch" \
  dotnet test tests/Revu.Tests.Integration/Revu.Tests.Integration.csproj \
  --filter "Review_FullPipeline_PostsFindings" \
  --logger "console;verbosity=detailed" > /tmp/revu-test.log 2>&1
```

### Step 3 — Show the tail

```bash
tail -15 /tmp/revu-test.log
```

### Step 4 — Run verify

```bash
python3 .claude/skills/test/scripts/verify.py --log /tmp/revu-test.log
```

### Step 5 — Abandon PR + delete branch

```bash
az repos pr update --id $pr_id --status abandoned --org "$org"
az repos ref delete --name "refs/heads/$branch" --object-id "$sha" \
  --repository "$repo_id" --org "$org" --project "$project"
```

Print the verify output to the user — that IS the full report. Do not add your own summary
afterwards. **Done. Stop here.**

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

## Verify output structure

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
