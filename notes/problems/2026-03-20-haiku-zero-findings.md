---
date: 2026-03-20
status: intermittent
tags: [haiku, structured-output, findings, flaky]
---

# Problem: Haiku intermittently produces zero findings on ado-sc test PR

## Problem Statement

CoreStrategy reviewer using `claude-haiku-4-5` intermittently returns 0 findings against the
`feature/order-tracking-notifications` branch in `ado-sc` (15 files, planted bugs across 4
services). Two consecutive runs failed through different mechanisms, but a third run succeeded
with 5/5 findings and 50% recall (3/4 required). Not a regression — haiku is flaky.

## Observed Behavior

**Run 1 (run-20260319-234756) — 0 findings, self-revision:** Model produced 11 findings in
its first text message, then in the *same* turn emitted a `load_skill("dotnet-architecture")`
tool call. After reading skill resources (http-client.md, auth-patterns.md, async-patterns.md),
it over-corrected and emitted a final response of `{"findings":[],"summary":""}`. The system
takes the last text message as output, so all 11 findings were discarded.

**Run 2 (run-20260319-234954) — 0 findings, parse failure:** Model followed correct tool-first
workflow (load_skill + 4x read_skill_resource), then produced 9 findings in a single text
response. However the JSON was malformed — the `summary` string value was never terminated with
a closing `"`, followed by ~15k zero-width space characters (U+200B). JSON parse failed.

**Run 3 (run-20260320-000321) — 5 findings, CLEAN:** Model followed correct workflow. Loaded
skills first, spawned 2 explorers (22 tool calls), produced 5/5 findings hitting the cap.
Recall 50% (3/4 required, 1/4 optional). Verdict: CLEAN.

## What Was Tried

Three test runs with identical diff against `ado-sc` profile. Two failed, one succeeded.

## Relevant Code / Files

- Session 1: `tests/Aporia.Tests.Integration/bin/Debug/net10.0/sessions/run-20260319-234756/01.json`
- Session 2: `tests/Aporia.Tests.Integration/bin/Debug/net10.0/sessions/run-20260319-234954/01.json`
- Session 3: `tests/Aporia.Tests.Integration/bin/Debug/net10.0/sessions/run-20260320-000321/` (3 files, success)
- Strategy: `Aporia/Review/CoreStrategy.cs`
- Prompts: `Aporia/Review/Prompts.cs`

## Notes for Next Session

- This is flakiness, not a regression. Haiku sometimes fails to follow tool-first ordering or
  truncates output. The successful run proves the pipeline and prompts work.
- Run 1's self-revision pattern is worth watching: skills loaded *after* findings caused the
  model to discard all of them. Consider whether skill loading should be forced earlier.
- Run 2's truncation (zero-width spaces, unterminated JSON) may be a haiku output limit issue.
  Could add JSON recovery/sanitization as a defensive measure.
- Both failing runs had 0 FetchFile calls — haiku sometimes skips codebase exploration entirely.
  The successful run had 12 FetchFile calls across explorers.

## Root Cause Analysis

### Issue 1 — Self-revision (Run 1): Anthropic-specific, not haiku-specific

The Anthropic API allows models to return text content *and* tool calls in the same response, with
`stop_reason: tool_use`. MEAI's `FunctionInvokingChatClient` sees `FinishReason=ToolCalls` and
continues its loop: executes the tools, calls the LLM again, gets a new text response. The new
text overwrites the original findings because `ExtractResult` takes `Messages.LastOrDefault().Text`.

OpenAI models separate text and tool calls into distinct turns — a response is either text OR
tool calls, never both — so this failure mode doesn't exist with OpenAI/Azure OpenAI providers.
Any Anthropic model can hit this, but smaller models (haiku) are more likely to violate the
prompt's "use tools first, produce output last" ordering.

The full message sequence in the MEAI loop:
1. LLM → `text(11 findings) + tool_call(load_skill)` — `FinishReason=ToolCalls`
2. MEAI executes `load_skill`, appends result, calls LLM again
3. LLM → `tool_call(read_skill_resource)` — `FinishReason=ToolCalls`
4. MEAI executes tool, calls LLM again
5. LLM → `text(empty findings)` — `FinishReason=Stop`, loop ends
6. `ExtractResult` reads message from step 5 → 0 findings

The valid findings from step 1 are still in `response.Messages` but are never read.

### Issue 2 — JSON truncation (Run 2): Haiku-specific

Haiku 4.5 has a max output of 8192 tokens. `ChatClientExtensions.cs:68` configures
`defaultMaxOutputTokens: 16_384` but the Anthropic API silently clamps to the model's limit.
With a large summary (markdown tables, `<details>` tags, pipe tables), the output hits 8192 and
truncates mid-JSON. The zero-width space padding is a known generation artifact at the output cap.
Sonnet/Opus have higher output limits (64k+) so are much less likely to hit this.

## Possible Fixes

### Fix A: `context.Terminate` in skill tools (best option for Issue 1)

MEAI's `FunctionInvokingChatClient` exposes `FunctionInvokingChatClient.CurrentContext` — an
ambient `FunctionInvocationContext` available during tool execution. Setting `context.Terminate =
true` stops the loop after the current tool completes. The LLM is not called again.

Approach: wrap `load_skill` / `read_skill_resource` (or all skill tools) so that if valid
structured output text already exists in the conversation (i.e., the model emitted findings
alongside the tool call), `Terminate` is set after executing the tool. The findings from the
earlier turn are preserved because the loop exits before the LLM can revise them.

This is the official MEAI mechanism for early loop termination (see dotnet/extensions#5764).
It's surgical — only activates when the model produced output before calling tools.

Caveat: `ExtractResult` currently reads `Messages.LastOrDefault()`, which after termination
would be the tool result message (no text). Would need to scan backwards for the last assistant
message with parseable text, or always scan all messages for the best parse.

### Fix B: `DelegatingChatClient` guard (alternative for Issue 1)

Insert a middleware between the raw LLM client and `FunctionInvokingChatClient`:
```
reviewerClient → StructuredOutputGuard → FunctionInvokingChatClient → agent
```
When the raw LLM response contains valid structured JSON text AND tool calls, the guard strips
`FunctionCallContent` items and sets `FinishReason=Stop`. The loop never starts. Simpler than
Fix A but less composable — it's a blunt "if you have output, don't call tools" rule.

### Fix C: Pre-load skills into context (eliminates Issue 1 entirely)

Load all skill content into the system prompt or via `AIContextProvider` before the review call.
The model never needs to call `load_skill` mid-review, so the sequencing problem disappears.
Costs more input tokens but skills are small (~2k tokens total). Downside: loses the on-demand
loading that keeps irrelevant skill content out of context.

### Fix D: Model-aware max output tokens (Issue 2)

Either detect the model and set appropriate `MaxOutputTokens` (8192 for haiku, 64k for
sonnet/opus), or set a conservative default that works for all models. Also consider adding JSON
recovery (strip zero-width characters, attempt to close unterminated strings) as a defensive
layer in `TryParseJson`.

### No existing MEAI/MAF issue for this

Searched dotnet/extensions and microsoft/agent-framework — no issue covers the "structured output
discarded during tool loop" scenario. Closest related:
- dotnet/extensions#5764 — unnecessary roundtrip after void functions (introduced `Terminate`)
- dotnet/extensions#7395 — structured output not working with Anthropic at all (SDK issue, fixed)
- dotnet/extensions#7184 — content roundtripping in middleware (different concern)

This is a legit gap. The interaction between structured output + tool calling + Anthropic's
text-alongside-tools behavior isn't handled. Worth filing on dotnet/extensions.
