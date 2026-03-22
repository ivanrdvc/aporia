---
date: 2026-03-20
status: draft
tags: [feedback, learning, continuous-improvement]
---

# Learnings Feedback Loop

## Problem Statement

Aporia posts review comments but has no mechanism to learn from whether those comments were useful.
Competitors (CodeRabbit, Qodo, Greptile, Ellipsis) all have feedback loops that improve review
quality over time. Without this, Aporia will keep making the same unhelpful comments and never
adapt to a team's actual preferences.

## Decision Drivers

- **Start simple**: no new infrastructure — Cosmos is already in place, no vector DB needed yet.
  A typical repo will have dozens of learnings, not thousands, so they all fit in prompt context.
- **Explicit feedback first**: CodeRabbit-style natural-language learnings extracted from developer
  replies to Aporia's comments. Higher signal than implicit reactions, and the reply mechanism
  already exists conceptually via the chat webhook path.
- **Leverage unused config fields**: `ProjectConfig.Rules` and `ProjectConfig.Context` are parsed
  but never read — these can serve as the static rules injection point alongside dynamic learnings.
- **Match existing patterns**: new `LearningStore` follows the same Cosmos store pattern as
  `RepoStore`, `ReviewStore`, `PrStateStore`.

## Research Summary

### Competitor Analysis

| Tool | Mechanism | Storage | Key Detail |
|---|---|---|---|
| CodeRabbit | Natural-language learnings from PR comment replies | LanceDB (vector) | Scope: repo or org. Admin can edit/delete via dashboard. |
| Qodo Merge | Auto-tracks accepted suggestions | Wiki file (`.pr_agent_auto_best_practices`) | Monthly synthesis of accepted suggestions into best practices. |
| Greptile | Thumbs-up/down reactions on comments | Vector DB (team-partitioned) | Embedding-based filtering. Addressed-comment rate: 19% → 55%. |
| Ellipsis | Reactions + inferred rules from human comments | Embedding search | Also extracts rules from in-repo style guide files. |

**Common thread**: nobody fine-tunes models. All use retrieval-augmented approaches (embeddings or
structured rules loaded into prompt context at review time).

### Codebase Findings

**Cosmos stores** (`Infra/Cosmos/`): All follow `IXxxStore` / `XxxStore` pattern with `CosmosDb`
dependency. Registration in `ServiceCollectionExtensions.AddCosmos()`. Container creation in
`CosmosInitializer`. Partition keys vary per store (`/id`, `/repositoryId`, `/repoId`).

**Prompt injection point**: `Prompts.ReviewerInstructions` is a `const string` in
`Review/Prompts.cs`. `CoreStrategy` passes it as `Instructions` to `ChatClientAgentOptions`.
To inject learnings dynamically, the instructions must become a runtime-built string rather than
a const.

**ProjectConfig** (`ProjectConfig.cs`): Has `Rules: List<string>` and `Context: string?` fields
that are parsed from `.aporia.json` but never consumed. These are ready-made for static team rules.

**Webhook handling** (`Functions/WebhookFunction.cs`): Currently handles `pull_request` events for
GitHub and PR update events for ADO. GitHub webhook validates HMAC-SHA256 signature. A new
function or event handler would be needed to process `issue_comment` events (replies to review
comments).

**GitHub connector** (`Git/GitHubConnector.cs`): `PostReview` embeds fingerprints in comments via
`<!-- aporia:fp:xxx -->` markers. Fetches existing comments to deduplicate. No reaction or reply
handling exists.

**Comment fingerprinting** (`Models.cs`): `Finding.Fingerprint()` produces SHA256 of normalized
`path|message`. This can link a reply back to the original finding.

**Service registration** (`Program.cs`): Stores registered as singletons. Strategies as keyed
scoped. `CosmosInitializer` (hosted service) ensures containers exist at startup.

## Implementation Steps

1. **Add Learning model and store**
   - Files: `Infra/Cosmos/LearningStore.cs`
   - Create `Learning` record: `Id`, `RepositoryId`, `Text`, `Source` (reply fingerprint or
     manual), `CreatedAt`, `CreatedBy`
   - Create `ILearningStore` / `LearningStore` following existing Cosmos store pattern
   - Container: `learnings`, partition key: `/repositoryId`, no TTL
   - Add container name to `CosmosOptions`

2. **Register store and container**
   - Files: `Infra/Cosmos/CosmosOptions.cs`, `Infra/Cosmos/CosmosInitializer.cs`,
     `Infra/ServiceCollectionExtensions.cs`
   - Add `LearningsContainer` const to `CosmosOptions`
   - Add container creation in `CosmosInitializer`
   - Register `ILearningStore` as singleton in `AddCosmos()`

3. **Add feedback webhook handler (GitHub)**
   - Files: `Functions/WebhookFunction.cs`
   - Add new function `WebhookGitHubComment` for `X-GitHub-Event: issue_comment`
   - Filter: only process comments that are replies to Aporia's own comments (detect via
     `<!-- aporia:fp:xxx -->` marker in parent comment)
   - Extract the reply text and the parent fingerprint
   - Enqueue to a new `learning-queue`

4. **Add learning extraction function**
   - Files: `Functions/LearningFunction.cs`
   - Queue-triggered function on `learning-queue`
   - Takes the developer's reply + original Aporia comment context
   - Calls LLM to extract a concise learning rule from the feedback
   - Persists via `ILearningStore`

5. **Inject learnings into review prompt**
   - Files: `Review/Prompts.cs`, `Review/CoreStrategy.cs`
   - Change `ReviewerInstructions` from `const` to a method that accepts learnings
   - Add a `<team_learnings>` section with loaded learnings
   - In `CoreStrategy`, load learnings from `ILearningStore` before building instructions

6. **Wire up static rules from ProjectConfig**
   - Files: `Review/Prompts.cs`, `Review/CoreStrategy.cs`
   - Also inject `ProjectConfig.Rules` and `ProjectConfig.Context` into the prompt
   - These are already parsed but never used — now they become part of the review context

## Open Questions

- [ ] Should learnings have org-wide scope (apply across all repos in an org) or start repo-only?
  Repo-only is simpler. Org-wide requires a secondary lookup.
- [ ] Should the feedback webhook share the existing HMAC validation with the PR webhook, or use
  a separate secret? (Likely same secret — same GitHub App.)
- [ ] How to handle learning conflicts or contradictions? (e.g., two learnings that say opposite
  things.) For v1, probably just let the LLM reconcile at prompt time.
- [ ] Should there be a way to view/manage learnings outside of PR comments? (Dashboard, CLI,
  API?) Not for v1.
- [ ] Should we also capture thumbs-down reactions (lower friction than replies)? GitHub sends
  these as `pull_request_review_comment` events with reaction data. Could be Phase 2.
