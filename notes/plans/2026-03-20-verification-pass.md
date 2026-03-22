---
date: 2026-03-20
status: draft
tags: [review, quality, false-positives]
---

# Verification Pass for Review Findings

## Problem Statement

The reviewer agent produces false positives — misreading diffs, missing surrounding context, or
flagging style issues as bugs. There is no validation step between the strategy producing findings
and Reviewer posting them. Anthropic's code review system uses a dedicated verification step to
filter false positives before surfacing findings, achieving <1% incorrect rate.

## Decision Drivers

- False positives erode trust and cause developers to ignore reviews
- Must work across all strategies (Core, Copilot, future), so it belongs in `Reviewer`, not inside
  a strategy
- Must stay cheap and fast — use `ModelKey.Default`, not reasoning model
- Fail-open: if verification fails, keep the finding (don't silently drop)
- Per-finding isolation is more reliable than batching (no cross-contamination)

## Solution

After `strategy.Review()` returns findings and before Reviewer's post-processing (sort, cap,
split), run each finding through a verifier LLM call that tries to disprove it. Only findings that
survive are passed forward.

```
strategy.Review() → List<Finding>
    ↓
Verifier.Verify() → discard disproved findings
    ↓
existing post-processing (sort, cap, post)
```

Per-finding calls run in parallel via `SemaphoreSlim(3)` (same pattern as `GuardedExploreTool`).
Each call uses `ModelKey.Default` with a `FetchFile` tool and 2 max roundtrips, structured output
`VerificationResult(bool Verified, string Reason)`.

## Research Summary

**Pipeline flow** (from codebase exploration):
- `Reviewer.Review()` (`Review/Reviewer.cs:34-87`) orchestrates: calls strategy (line 46), filters
  to diff paths (lines 50-56), sorts by severity, caps at `maxComments`, splits inline/summary
- `CoreStrategy` (`Review/CoreStrategy.cs`) uses MAF agent with `ModelKey.Reasoning`, structured
  `ReviewResult` output, tools (FetchFile, ListDirectory, SearchCode, QueryCodeGraph, Explore)
- `ReviewerTools` (`Review/ReviewerTools.cs`) wraps `IGitConnector` with diff-cached file access
- `GuardedExploreTool` (CoreStrategy.cs:81-181) — `SemaphoreSlim`-based concurrency pattern to
  follow
- `Reviewer` already injects `[FromKeyedServices(ModelKey.Default)] IChatClient` (line 25)
- DI: `Reviewer` registered as scoped (`Program.cs:31`)
- Telemetry counters in `Infra/Telemetry/Telemetry.cs`

**Key types:**
- `Finding(FilePath, StartLine, EndLine?, Severity, Message, CodeFix?)` in `Models.cs:58-73`
- `ReviewResult(List<Finding>, string Summary)` in `Models.cs:75-78`
- `Diff(List<FileChange>, Cursor?)` — `FileChange` has `Path`, `Kind`, `Patch`, `Content`
- `ProjectConfig.ReviewConfig` has `Strategy` and `MaxComments` (nullable, merged with defaults)

**Anthropic's approach** (from blog post): dispatches agents in parallel to find bugs, then runs a
separate verification step to filter false positives before ranking by severity. ~54% of PRs get
substantive comments, <1% marked incorrect.

## Implementation Steps

1. **Add `VerificationResult` record**
   - Files: `Aporia/Models.cs`
   - Add `public record VerificationResult(bool Verified, string Reason);` alongside existing
     review types

2. **Add verifier prompt**
   - Files: `Aporia/Review/Prompts.cs`
   - Add `VerifierInstructions` constant — adversarial prompt instructing the model to disprove
     the finding (check: is issue present in code? does context handle it? is it pre-existing?
     is it style dressed as bug? does code fix compile?)
   - Add `BuildVerificationPrompt(Finding, FileChange?)` — formats finding + file patch into user
     message

3. **Create `Verifier` class**
   - Files: `Aporia/Review/Verifier.cs` (new)
   - Constructor: `[FromKeyedServices(ModelKey.Default)] IChatClient`, `IServiceProvider`,
     `ILogger<Verifier>`
   - Public method: `Task<List<Finding>> Verify(List<Finding>, ReviewRequest, Diff, CancellationToken)`
   - Per-finding parallel calls with `SemaphoreSlim(3)`
   - Each call: build prompt, call `chatClient` with `FetchFile` tool + 2 max roundtrips +
     structured output `VerificationResult`
   - Extract result; keep finding if `Verified == true` or extraction fails (fail-open)
   - Log discarded findings at Information level

4. **Wire into Reviewer**
   - Files: `Aporia/Review/Reviewer.cs`
   - Add `Verifier verifier` to constructor parameters
   - After line 46 (`strategy.Review()`), insert:
     ```csharp
     if (result.Findings.Count > 0)
         result = result with { Findings = await verifier.Verify(result.Findings, req, diff, ct) };
     ```

5. **Add telemetry counter**
   - Files: `Aporia/Infra/Telemetry/Telemetry.cs`
   - Add `FindingsDiscarded` counter: `"aporia.findings.discarded"`

6. **Register in DI**
   - Files: `Aporia/Program.cs`
   - Add `builder.Services.AddScoped<Verifier>();` next to `Reviewer` registration

## Open Questions

- [ ] Should there be a ProjectConfig toggle (`review.verify: false`) to let repos opt out, or
  always verify? (Leaning: skip for now, always verify, add toggle later if needed)
- [ ] Should discarded findings appear anywhere in the PR summary (e.g. collapsed section), or
  only in logs/telemetry?
