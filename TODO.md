# TODO

## Fixes

- Fix hardcoded max turns in prompt
- Merge overlapping findings on same line range before posting
- Pin `GetFile`/`ListFiles` to iteration commit SHA instead of branch tip

## Features

- Build repo map (AST structural index) for compact codebase awareness
- Externalize prompts (load from blob/config instead of compiled `Prompts.cs`)
- Switch auth from PAT-only to `DefaultAzureCredential` with PAT fallback
- Learnings from feedback — inject past review learnings into reviewer instructions
- Work item integration — fetch linked work items as additional review context
- Local repo cloning support for tool-based reviews
- Auto-detect per-repo metadata (language version, framework) to reduce false positives
