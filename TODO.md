# TODO

## Fixes

- Fix hardcoded max turns in prompt
- Merge overlapping findings on same line range before posting
- Pin `GetFile`/`ListFiles` to iteration commit SHA instead of branch tip
- `CodeGraphIndexer` was registered as singleton with non-keyed `IGitConnector` — broke DI validation locally after keyed services were introduced. Temp-fixed by resolving via `IServiceProvider` at runtime. Should align with `IndexFunction`'s provider from `IndexRequest`.
- Split `IGitConnector` — interface is too broad (pipeline + tools + chat). Consider splitting into focused interfaces (e.g. core pipeline, code browsing, chat) while keeping shared implementations.
- ~~`QueryCodeGraph` tool is registered unconditionally in `CoreStrategy` (reviewer + explorer) and `Reviewer` (chat) — LLM can call it even when `EnableCodeGraph` is false. Should only add the tool when `codeGraph != null`.~~ (done)
- Code graph indexing only runs on repo registration. If a repo was registered before the feature existed, or `EnableCodeGraph` was false at the time, the graph is never built. Trigger indexing from the review pipeline — if a review runs and the code graph is empty, enqueue an `IndexRequest` to `index-queue` so the next review benefits. Don't block the current review.

## Features

- Verification pass — after the reviewer produces findings, run a second pass that tries to disprove each finding before posting. Goal: reduce false positives. The verifier should attempt to refute each finding by checking context, reading referenced code, and confirming the issue is real. Discard findings it can disprove.

- ~~Build repo map (AST structural index) for compact codebase awareness~~ (done: code graph)
- LSP integration for type-resolved call graphs (fixes cross-file type resolution limitations in tree-sitter; requires local repo cloning)
- Externalize prompts (load from blob/config instead of compiled `Prompts.cs`)
- Switch auth from PAT-only to `DefaultAzureCredential` with PAT fallback
- Semantic search — vector embeddings for similar-pattern detection, consistency checks, related tests
- Learnings from feedback — inject past review learnings into reviewer instructions
- ~~Work item integration — fetch linked work items as additional review context~~ (done)
- Cross-provider work item enrichment — parse `AB#` references from GitHub PR descriptions/commits and fetch ADO work items (requires dual auth: GitHub + ADO credentials for the same review)
- Local repo cloning support for tool-based reviews
- Auto-detect per-repo metadata (language version, framework) to reduce false positives
- Feature-level config system — per-repo feature toggles on `Repository` entity (app-owner controls) and `.aporia.json` `ProjectConfig` overrides (project-owner controls). Three-tier precedence: global `AporiaOptions` > per-repo `Repository` > `.aporia.json`. Applies to incremental reviews, code graph, chat, and future flags.
- ~~GitHub webhook: subscribe to `pull_request.ready_for_review` (draft→ready transition) to trigger reviews when PRs leave draft state~~ (done)
- GitHub chat: subscribe to `pull_request_review_comment.edited` for cache invalidation when users edit comments in review threads
- GitHub issue context — scan PR body/title for `#N` references, fetch issue details (title, body, comments) via GitHub API, inject into reviewer context for better understanding of *why* the change was made
- CodeFix fuzzy anchoring — instead of trusting LLM line numbers for suggestions, match the original code snippet against actual file content (exact → whitespace-trimmed → anchor-based) and reposition the suggestion to the real location. Reduces misplaced suggestions when line numbers drift.
- "Review skipped" feedback — when a PR is skipped (no matching files, too large, etc.), post a comment explaining why with actionable metrics so the author knows what to fix
- Claude Code C# LSP agent — create a dedicated agent with `cclsp` MCP server bridging Roslyn LSP (`csharp-ls`) for type-aware navigation (go-to-definition, find-references, diagnostics). Blocked on Claude Code LSP stability (tracking: anthropics/claude-code#15619, #15168)
