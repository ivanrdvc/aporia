---
date: 2026-03-24
status: draft
tags: [review, prompts, recall, haiku]
---

# Enumerate-First Review Workflow

## Problem Statement

The reviewer consistently finds only 2-3 bugs out of 7+ that are visible in the diff. Recall is
29% across runs. Session analysis shows the root cause: the model scans the diff, identifies the
first couple of issues, then spends its remaining LLM turns confirming them via skill lookups
before producing output. It never returns to scan the rest of the files.

Example from a Haiku run (run-20260324-000338):
- Turn 1: Reasoning scans all files, notes HttpClient and CancellationToken issues, calls
  `load_skill("dotnet-architecture")`
- Turns 2-3: Calls `read_skill_resource` twice to confirm the two findings
- Turn 4: Starts to re-scan ("Looking at ShippingNotificationS..."), gets cut off, outputs
  JSON with 2 findings

The model sees the bugs in its reasoning but never finishes enumerating them because the current
workflow structure (`<workflow>` in `Prompts.cs`) tells it to analyze, then use tools, then
"before producing findings, strongly consider calling load_skill" — which sends it into a
confirmation loop on the first findings it spots.

All 9 expected bugs in the eval fixture are diff-visible. Zero require tool use to detect. The
problem is not bug discoverability or tool availability — it is premature depth-first commitment.

## Decision Drivers

- Haiku (the cheapest model) must improve recall without increasing cost significantly. The fix
  must work within Haiku's 8192-token output limit and ~4 LLM turns.
- The change is prompt-only — no strategy or infrastructure changes.
- Must not regress Sonnet/Opus behavior, which already has better recall.
- Skills are still valuable for reducing false positives, but must not consume turns before the
  model has finished scanning.
- The approach should be grounded in research: Plan-and-Solve prompting (ACL 2023) shows that
  forcing enumeration before execution reduces missing-step errors in zero-shot CoT.

## Research Summary

**Session analysis (run-20260324-000338, Haiku on ADO eShop PR #88):**
- 132,674 input tokens / 3,901 output tokens across 5 API calls
- 3 tool calls: `load_skill` x1, `read_skill_resource` x2 — zero codebase tools
- 4 LLM turns, 2 findings (HttpClient, CancellationToken)
- Missed: PII logging, IDOR, enum ordinal shift, DDD violation, `new HttpClient()` in handler,
  cross-service DB, logic bug (ANY vs ALL)

**Eval test (multi-agent-crossservice.json, Haiku):**
- 56% recall (5/9) — better than integration test but still misses 4 optional bugs
- Also zero codebase tool calls

**All expected bugs are diff-visible:** Every expected finding in `multi-agent-crossservice.json`
targets a file that is in the diff with full patch context. The bugs include hardcoded credentials,
wrong arguments, PII logging, IDOR, enum value shifts, missing status guards, and `new HttpClient()`
patterns — all readable directly from the patch.

**Relevant research:**
- **Plan-and-Solve (ACL 2023):** Replacing "think step by step" with "first devise a plan, then
  execute" significantly reduces missing-step errors. The planning phase forces enumeration of all
  subtasks before executing any.
- **Skeleton-of-Thought (ICLR 2024):** Two-phase generate-then-expand produces equal or better
  quality. The enumeration skeleton prevents budget exhaustion on early items.
- **Verification-First (2025):** A second "did we miss anything?" pass adds minimal token overhead
  (~20-50%) and improves recall.
- **Serial Position Effects (2024):** LLMs exhibit primacy bias — early content in input gets
  disproportionate attention. Files appearing late in the diff are under-indexed.
- **CrashOverride security review guide:** Specifying discrete analysis categories (checklist)
  forces the model to visit each category, preventing premature stopping.

## Solution

Restructure the `<workflow>` section in `ReviewerInstructions` to enforce a two-phase approach:

**Phase 1 — Scan (breadth-first):** Go through every file in the diff and list all candidate
issues as a numbered list: file, line, one-liner description. No tool calls, no confirmation. This
is cheap output and forces the model to visit every file before committing to any finding.

**Phase 2 — Verify and report:** Now optionally use tools (skills, FetchFile, Explore) to confirm
or dismiss candidates. Produce final findings from verified issues.

This reorders the existing workflow steps so that enumeration comes before tool use, rather than
interleaving them.

## Implementation Steps

1. **Restructure `<workflow>` in `Prompts.ReviewerInstructions`**
   - File: `Aporia/Review/Prompts.cs` (lines 131-168)
   - Current steps: 1) analyze visible code, 2) check inventory before tools, 3) use Explore,
     4) load skills, 5) produce findings
   - New steps:
     1. **Scan all files** — read every file in the diff and list all candidate issues as a
        numbered shortlist (file + line + one-liner). Do not call tools yet. Do not stop after
        the first few — cover every file.
     2. **Verify candidates** — use FetchFile, SearchCode, Explore, or skills to confirm or
        dismiss ambiguous candidates. Skip verification for issues that are clearly bugs from
        the diff alone.
     3. **Produce findings** — emit the final findings JSON from verified candidates.
   - Keep the existing guidance about when to use FetchFile vs Explore vs SearchCode — just move
     it under the verify step.
   - Keep the skill loading guidance but reframe: "use skills to verify candidates or reduce
     false positives" rather than "before producing findings, load skills."

2. **Validate with eval test**
   - Run `multi-agent-crossservice.json` eval fixture with the updated prompt
   - Compare recall before/after — target: >50% recall on Haiku (currently 29% integration,
     56% eval)
   - Check that finding count increases without a proportional increase in false positives

3. **Validate with integration test**
   - Run `/test ado` with updated prompt
   - Verify posted comments cover more of the expected findings
   - Check that Sonnet/Opus behavior doesn't regress (run `/test gh` or eval with larger model)

4. **Update expectations if needed**
   - File: `.claude/skills/test/scripts/expectations.json`
   - If recall improves enough, consider promoting some optional findings to required

## Open Questions

- [ ] Should the scan phase be a separate system prompt turn (structured output requesting just a
      list) or inline instructions in the same prompt? A separate turn guarantees the list is
      produced but adds latency and token cost. Inline is cheaper but the model might skip it.
- [ ] Does Haiku's 8192-token output limit leave enough room for both the enumeration list and the
      final findings JSON in the same response? If not, the scan may need to be a separate turn.
- [ ] Should we add a "did we miss anything?" verification pass after findings are produced
      (Verification-First pattern), or is the scan phase sufficient?
- [ ] Serial position bias: should the diff file order be randomized or reversed to counteract
      primacy effects? This is orthogonal to the prompt change but could compound the improvement.
