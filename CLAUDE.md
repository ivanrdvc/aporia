# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this
repository.

## What This Is

AI code review service. Receives pull request webhooks, analyzes diffs with an LLM, posts inline
comments back to the PR. .NET 10 Azure Functions (isolated worker model).

## The Pipeline

Two Azure Functions. Config flows forward read-only.

```
WebhookFunction (HTTP trigger) → review-queue → ReviewFunction (Queue trigger)
```

`WebhookFunction` validates the webhook and enqueues a `ReviewRequest`. `ReviewFunction` runs the
review pipeline:

```
GetConfig → GetDiff → Review → PostReview
```

## Architecture

**IGitConnector** (`Git/`) — platform I/O. Fetches config from repo, fetches diffs, posts
comments, and exposes codebase exploration tools (`GetFile`, `SearchCode`, `ListFiles`). Dumb
transport — no review logic. Each provider (AzureDevOps, GitHub) implements this. `AdoWebhook.cs`
lives here — it's ADO transport detail, not a shared model.

**ProjectConfig** (root `ProjectConfig.cs`) — repo-level rules from `.revu.json`. Owns its own
parsing and merging with defaults. Provider fetches raw file, calls `ProjectConfig.Parse()`.

**Cosmos stores** (`Infra/Cosmos/`) — thin Cosmos-backed persistence. `RepoStore` gates the webhook
(only registered repos trigger reviews) and holds repo metadata. `ReviewStore` persists a review
event for every pipeline run (audit trail). `PrStateStore` tracks last reviewed iteration per PR
for incremental diffs (90-day TTL, self-cleans).

**Reviewer** (`Review/Reviewer.cs`) — resolves the strategy by key from a
`Func<string, IReviewStrategy>` factory, applies the comment cap and severity sort, wraps the
summary.

**IReviewStrategy** (`Review/`) — LLM concerns. `CoreStrategy` is the default — a reviewer
agent (`ModelKey.Reasoning`) with direct tools (`FetchFile`, `SearchCode`, `ListDirectory`) that
does the analysis itself. For cross-file analytical work (questions requiring reading and comparing
multiple files), it spawns explorer sub-agents (`ModelKey.Default`) via the `Explore` tool.
Explorers have the same tools but return structured `ExplorationResult` JSON — the schema prevents
raw file dumps. The reviewer does all analytical judgment; explorers compress context.
Strategies are named after their runtime: `CoreStrategy` (direct API via `IChatClient`),
`CopilotStrategy` (GitHub Copilot CLI), `ClaudeCodeStrategy` (future). Constants live in
`IReviewStrategy.cs`.

**Prompts** (`Review/Prompts.cs`) — all prompt text. Internal review heuristics live here, not in
ProjectConfig.

**Skills** (`Skills/`) — domain-specific review knowledge the reviewer loads on demand via MAF's
`FileAgentSkillsProvider`. Each skill is a `SKILL.md` following the Agent Skills open standard.
The provider advertises available skills in the reviewer's instructions and exposes `load_skill` /
`read_skill_resource` tools. Skills are reviewer capabilities that ship with Revu, not project
config.

## Design Principles

- Keep it simple. Business concerns get domain folders (`Git/`, `Review/`). Technical plumbing
  lives under `Infra/` with cohesive subfolders (`AI/`, `Cosmos/`, `Telemetry/`). No abstractions
  for their own sake.
- Config flows forward read-only — fetched once, passed down without mutation.
- Provider is dumb transport — fetches raw data, does not own parsing or review logic.
- Strategy owns all LLM concerns — prompts, tokens, calls, parsing, filtering.
- Reviewer analyzes directly with tools; explorers handle cross-file work and return structured
  conclusions. Multi-agent is a context escape hatch, not the default path.
- One file per domain concern, not one file per type — small related types are grouped (e.g.
  `Models.cs`, `ProjectConfig.cs`).
- Never run integration tests automatically.
