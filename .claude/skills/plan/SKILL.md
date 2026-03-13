---
name: plan
description: "Capture a plan from the current conversation to implement later: /plan"
user-invocable: true
disable-model-invocation: true
---

# Plan Capture Skill

You are capturing a plan from the current conversation so it can be implemented later — in another
session, by another AI, or as a reference for future work.

## Workflow

1. **Clarify requirements** — before writing anything, check if there are ambiguities in the
   conversation around constraints or approach. If there are genuine gaps that cannot be inferred,
   use `AskUserQuestion` once for all missing items. Do not ask about things that can be derived
   from context.

2. **Ground in code** — if the conversation didn't already explore the codebase, spawn an Explore
   agent to examine the affected code areas. Plans must reflect actual code structure, not
   assumptions. Skip this if exploration was already done in the conversation.

3. **Extract and write** — read the conversation and fill every field in `template.md`. Derive
   the title, problem statement, decision drivers, solution, implementation steps, and open
   questions from what is already there.

4. **Slug** — generate a short kebab-case slug from the title (e.g. `copilot-review-strategy`).

5. **Write** — save to `notes/plans/YYYY-MM-DD-<slug>.md` using `template.md`.

6. **Confirm** — print the file path and status. Nothing else.

## Rules

- Do NOT start implementing the plan. Capture only.
- Keep sections factual and concise — the plan must be readable cold by another AI or session.
- If research was done during the conversation (code exploration, docs, samples, library analysis),
  preserve findings in **Research Summary**. Do not lose work that was already done.
- If the user adds extra context verbally, include it under **Notes for Implementation**.
- Implementation steps must reference actual file paths, class names, and patterns from the
  codebase — not hypothetical structure.
- Remove optional sections (Research Summary, Notes for Implementation) if they have no content.
  Do not leave placeholder text.
