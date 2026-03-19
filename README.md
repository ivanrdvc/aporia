# Aporia

[![CI](https://github.com/ivanrdvc/aporia/actions/workflows/ci.yml/badge.svg)](https://github.com/ivanrdvc/aporia/actions/workflows/ci.yml) [![Deploy](https://github.com/ivanrdvc/aporia/actions/workflows/deploy.yml/badge.svg)](https://github.com/ivanrdvc/aporia/actions/workflows/deploy.yml) [![Release](https://github.com/ivanrdvc/aporia/actions/workflows/release.yml/badge.svg)](https://github.com/ivanrdvc/aporia/actions/workflows/release.yml)

AI code review for pull requests.

## Features

- **AI code review** — line-level findings with committable suggestions and a per-file summary, posted directly on the PR diff
- **Multi-provider** — supports GitHub and Azure DevOps
- **PR chat** — reply to a finding or mention `@aporia` anywhere on the PR for a follow-up conversation
- **Review skills** — on-demand domain knowledge (security, framework patterns, etc.) loaded by the reviewer when relevant
- **Code graph** — indexes repo structure (classes, methods, dependencies) so the reviewer understands cross-file context
- **Multiple review engines** — pluggable strategies: direct API or GitHub Copilot (Claude Code CLI planned)

## Run locally

Prerequisites: [.NET 10](https://dotnet.microsoft.com/download),
[Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local),
[Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (or Azure Storage).

1. Copy settings and fill in your credentials (AI keys, provider PAT/App, Cosmos):
   ```bash
   cp Aporia/local.settings.example.json Aporia/local.settings.json
   ```

2. Start the function host:
   ```bash
   cd Aporia && func start
   ```

See [docs/setup.md](docs/setup.md) for webhook tunnels and full deployment,
and [docs/observability.md](docs/observability.md) for local telemetry.

## Getting started

Aporia only reviews registered repositories. Unregistered repos are silently ignored. To enroll a
repo, register it via `POST /api/manage/repos` (requires a function key), then create service
hooks for PR events. Optionally drop a `.aporia.json` in the repo root to customize review behavior
(see [Configuration](#configuration)).

## Configuration

Drop a `.aporia.json` file in the root of your repository to customize review behavior. All fields
are optional; sensible defaults apply when omitted.

See [`.aporia.example.json`](.aporia.example.json) for all available options.

### Skills

Aporia ships with built-in review skills: domain-specific knowledge the reviewer loads on demand
when a PR touches a relevant area.

Each skill is a `SKILL.md` file under `Aporia/Skills/` containing domain-specific review criteria.
