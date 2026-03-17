# TODO

## Fixes

- Fix hardcoded max turns in prompt
- Merge overlapping findings on same line range before posting
- Pin `GetFile`/`ListFiles` to iteration commit SHA instead of branch tip
- `CodeGraphIndexer` was registered as singleton with non-keyed `IGitConnector` — broke DI validation locally after keyed services were introduced. Temp-fixed by resolving via `IServiceProvider` at runtime. Should align with `IndexFunction`'s provider from `IndexRequest`.

## Features

- ~~Build repo map (AST structural index) for compact codebase awareness~~ (done: code graph)
- LSP integration for type-resolved call graphs (fixes cross-file type resolution limitations in tree-sitter; requires local repo cloning)
- Externalize prompts (load from blob/config instead of compiled `Prompts.cs`)
- Switch auth from PAT-only to `DefaultAzureCredential` with PAT fallback
- Semantic search — vector embeddings for similar-pattern detection, consistency checks, related tests
- Learnings from feedback — inject past review learnings into reviewer instructions
- Work item integration — fetch linked work items as additional review context
- Local repo cloning support for tool-based reviews
- Auto-detect per-repo metadata (language version, framework) to reduce false positives
- Feature-level config system — per-repo feature toggles on `Repository` entity (app-owner controls) and `.revu.json` `ProjectConfig` overrides (project-owner controls). Three-tier precedence: global `RevuOptions` > per-repo `Repository` > `.revu.json`. Applies to incremental reviews, code graph, chat, and future flags.
