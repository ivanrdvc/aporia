---
date: 2026-03-15
status: open
tags: [review-quality]
---

# Problem: Reviewer can't distinguish intentional patterns from real bugs

## Problem Statement

The reviewer pattern-matches against known anti-patterns (e.g. `new HttpClient()` = leak, early return = silent skip, empty secret = bypass) and reports them as findings. Each pattern is real in general but wrong in the specific project context. The model has no way to distinguish "this looks like a bug" from "this is actually a bug" because three things compound:

1. **It doesn't know the project's design decisions.** E.g. "empty webhook secret is fine, function key auth is the fallback" — that's in the author's head, not in the code or prompt.
2. **It doesn't compare with existing code before reporting.** E.g. both ADO and GitHub connectors have the same `GetDiff` early return — if it checked the sibling implementation, it would see the pattern is intentional. The prompt tells it to do this (exploration guidance) but it doesn't.
3. **The bar for reporting is too low.** "This could be a problem" is enough to make the cut. The prompt says "only report findings you are confident will cause real problems" but that's subjective and not reinforced.

A prompt rewrite for #3 (anti-bias framing in workflow step 4) is planned separately in `2026-03-15-self-review-quality.md`. This problem focuses on #1 and #2 — giving the model the context it needs to make correct judgments.

## Observed Behavior

From the self-review sessions (26-file, ~4000-line diff adding GitHub provider):

**Sonnet 4.6 (run-20260315-162059):** 7 findings, 0 tool calls, 0 real bugs. Every finding was a known pattern applied without project context:
- "HMAC bypass when secret is empty" — by design, function key auth is the fallback. Model didn't know this.
- "GetDiff silently skips re-reviews" — identical behavior in both connectors. Model didn't check the sibling.
- "HttpClient leak in singleton" — standard .NET pattern. Model pattern-matched `new HttpClient()` without checking lifecycle.
- "ParseDiffHunks count=0 edge case" — pure-deletion hunks have no commentable lines. Model didn't verify downstream usage.

**Haiku (run-20260315-161529):** 6 findings emitted before its single tool call completed. Hallucinated against code already in the prompt — claimed keyed services weren't registered when `Program.cs` full source showed both `AddKeyedSingleton` calls.

## What Was Tried

- **Anti-bias prompt rewrite (planned, not yet tested):** Strengthens workflow step 4 to frame zero-findings as the expected outcome. Addresses #3 (low bar) only. Doesn't give the model project context or make it verify against existing code.
- **Evidence field on findings (rejected):** Forcing the model to cite code evidence per finding. Doesn't work — a model that's wrong about `new HttpClient()` being a leak will cite the constructor line as "evidence" just as confidently.
- **Verification gate / mandatory tool calls (rejected for cost):** Quality improved in earlier testing but doubled turns and token usage. Too expensive.

## Relevant Code / Files

- Prompts: `Aporia/Review/Prompts.cs` — reviewer instructions, exploration guidance, severity definitions
- Strategy: `Aporia/Review/CoreStrategy.cs` — reviewer agent setup, tool configuration
- Project config: `Aporia/ProjectConfig.cs` — repo-level rules from `.aporia.json`
- Session (Sonnet): `tests/Aporia.Tests.Integration/bin/Debug/net10.0/sessions/run-20260315-162059/`
- Session (Haiku): `tests/Aporia.Tests.Integration/bin/Debug/net10.0/sessions/run-20260315-161529/`
- Related problem: `notes/problems/2026-03-15-self-review-quality.md`

## Notes for Next Session

- **#1 Project context** — how do we get design decisions into the review? Options: expand `.aporia.json` with a `designDecisions` or `knownPatterns` section the author maintains; let `ProjectConfig` carry free-text context the prompt includes; use the skills system to encode project-specific knowledge. The tradeoff is maintenance burden vs. review accuracy.
- **#2 Sibling verification** — the exploration guidance already tells the model to compare with sibling implementations. Sonnet ignored it on this run (0 tool calls). Is this a prompt strength issue, or does the model need a harder nudge — e.g. "before reporting a pattern violation, you MUST check at least one sibling implementation"? Would that just become the expensive verification gate again?
- The prompt rewrite for #3 should land first — it's zero cost and may shift the baseline enough that #1 and #2 become clearer to evaluate.
- Run with Opus to see if a stronger model naturally does #2 (checks siblings) without prompt changes. If Opus gets it right, the problem may be model capability for Sonnet/Haiku, not a systemic design issue.
- Thinking blocks aren't captured (Anthropic C# SDK MEAI adapter drops them). Can't debug whether the model considered checking siblings and decided not to, or never considered it. Fixing observability would help diagnose #2.
