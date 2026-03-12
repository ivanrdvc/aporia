# Eval Tests

Evaluation harness for Revu review quality. Runs real reviews against captured
PR fixtures, then scores the output with deterministic and LLM-as-judge evaluators.
Uses `Microsoft.Extensions.AI.Evaluation` with disk-backed reporting.

## Rules

- **Never run eval tests automatically** — they call real LLMs and cost money.
- Never add eval tests to CI or to a "run all tests" command.

## Quick Start

```bash
dotnet test tests/Revu.Tests.Eval
```

Needs API credentials — see [root README](../../README.md#2-configure-secrets).

## Reporting

Results are persisted to `bin/.../EvalResults/` via `DiskBasedReportingConfiguration`.
LLM-as-judge responses are cached on disk (14-day TTL) so re-runs skip redundant
judge calls when the review output hasn't changed.

Generate an HTML report from the stored results:

```bash
dotnet aieval report --results-path tests/Revu.Tests.Eval/bin/Debug/net10.0/EvalResults --output report.html --open
```

## Evaluators

All evaluators implement `IEvaluator` from `Microsoft.Extensions.AI.Evaluation`.

| Evaluator | Type | Fails? | What it measures |
|---|---|---|---|
| `FindingGroundednessEvaluator` | Deterministic | Yes | Findings target changeset files, no duplicates, count > 0 |
| `ExpectedFindingsEvaluator` | Deterministic | Yes | Planted bugs found (file path + keyword match). Only required findings fail. |
| `AgentBehaviorEvaluator` | Deterministic | If mode mismatch | Agent mode, investigation count, tool usage breakdown |
| `FindingQualityEvaluator` | LLM-as-judge | No | Actionability, specificity, code formatting (1-5 avg) |

## Adding Fixtures

Drop a JSON file in `tests/Revu.Tests.Eval/TestData/` — it becomes a test case
automatically. The JSON must match the `FixtureData` record shape:

```jsonc
{
  "request": { "provider": "ado", "project": "...", ... },
  "config":  { "context": "...", "rules": [...], "review": { "maxComments": 8 } },
  "diff":    { "files": [{ "path": "...", "change": "edit", "diff": "..." }] },
  "files":   { "src/Foo.cs": "full content...", ... },
  "expectations": {
    "expectedMode": "multi-agent",
    "expectedFindings": [
      { "file": "/src/Foo.cs", "keyword": "injection", "description": "SQL injection" },
      { "file": "/src/Bar.cs", "keyword": "hardcoded", "description": "...", "required": false }
    ]
  }
}
```

- **files** — pre-captured file contents served by `FixtureGitConnector` when the
  agent calls `GetFile`, `SearchCode`, or `ListFiles`. No network needed.
- **expectations.expectedFindings** — planted bugs the agent should find. Matched by
  file path (case-insensitive, leading `/` trimmed) + keyword in the finding message.
- **required** — defaults `true`. Required findings fail the test if missed. Optional
  findings (`"required": false`) are bonus — they show in recall but don't fail. Use
  for subtle issues that may get crowded out by `maxComments`.
