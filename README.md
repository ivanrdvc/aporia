# Revu

[![CI/CD](https://github.com/ivanrdvc/revu/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/ivanrdvc/revu/actions/workflows/ci-cd.yml)

AI code review for pull requests.

## Features

- **Inline AI comments** — line-by-line review findings posted directly on the PR diff
- **Committable suggestions** — one-click apply code suggestions from the review
- **PR summaries** — per-file overview of what changed in the PR
- **Incremental reviews** — only reviews new iterations, skips already-reviewed changes
- **Chat with bot** *(planned)* — interactive conversation with the reviewer on the PR
- **Review skills** — on-demand domain knowledge (security, framework patterns, etc.) loaded by the reviewer when relevant

## Run locally

### 1) Install prerequisites

- [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (for local queue storage)
- [Docker](https://docs.docker.com/get-docker/) (optional, only for OpenObserve)
- [Python 3](https://www.python.org/downloads/) (optional, only for the eval verify script in `.claude/skills/test/scripts/verify.py`)

### 2) Configure secrets

All projects (eval, unit tests, integration tests) share a single `UserSecretsId` and read AI
credentials from [.NET user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets).
The only exception is the Azure Functions host, which reads from `local.settings.json`.

```bash
cd Revu
# Azure OpenAI
dotnet user-secrets set "Ai:AzureOpenAI:Endpoint" "https://your-endpoint.openai.azure.com"
dotnet user-secrets set "Ai:AzureOpenAI:ApiKey" "your-key"
# — or OpenAI directly —
dotnet user-secrets set "Ai:OpenAI:ApiKey" "sk-..."
```


### 3) Configure Azure Functions host

Copy the template and fill in ADO org, PAT (Code Read/Write scope), and AI keys:

```bash
cp Revu/local.settings.example.json Revu/local.settings.json
```

This file is gitignored.

### 4) Optional: start OpenObserve for observability

```bash
docker run --rm -it -p 5080:5080 -e ZO_ROOT_USER_EMAIL=root@example.com -e ZO_ROOT_USER_PASSWORD=Complexpass#123 --name openobserve openobserve/openobserve:latest
```

Observability data (logs, traces, metrics) is available at http://localhost:5080 (see [docs/observability.md](docs/observability.md)).

## Getting started

Revu only reviews registered repositories. Unregistered repos are silently ignored. To enroll a
repo, register it via `POST /api/manage/repos` (requires a function key), then create an Azure
DevOps **Service Hook** for PR created/updated events pointing at
`https://<your-revu-host>/api/webhook/ado`. Optionally drop a `.revu.json` in the repo root to
customize review behavior (see [Configuration](#configuration)).

## Configuration

Drop a `.revu.json` file in the root of your repository to customize review behavior. All fields
are optional; sensible defaults apply when omitted.

```jsonc
{
  // Free-text context about the repo (fed to the reviewer as background)
  "context": "E-commerce API, .NET 10, DDD with CQRS",

  // Custom review rules (appended to built-in prompt)
  "rules": [
    "Prefer record types for DTOs",
    "All public methods must have XML doc comments"
  ],

  "review": {
    // Max inline comments per review (default: 5)
    "maxComments": 10
  },

  "files": {
    // File extensions to review (default: .cs, .ts, .js, .jsx, .tsx)
    "allowedExtensions": [".cs", ".ts"],

    // Glob patterns to skip (merged with built-in ignores like bin/, node_modules/)
    "ignore": ["**/Migrations/**"]
  }
}
```

### Skills

Revu ships with built-in review skills: domain-specific knowledge the reviewer loads on demand
when a PR touches a relevant area. 

Each skill is a `SKILL.md` file under `Revu/Skills/` containing domain-specific review criteria.
