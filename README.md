# Revu

[![CI](https://github.com/ivanrdvc/revu/actions/workflows/ci.yml/badge.svg)](https://github.com/ivanrdvc/revu/actions/workflows/ci.yml)

AI code review for pull requests.

## Features

- **Inline AI comments** — line-by-line review findings posted directly on the PR diff
- **Committable suggestions** — one-click apply code suggestions from the review
- **PR summaries** — per-file overview of what changed in the PR
- **Incremental reviews** — only reviews new iterations, skips already-reviewed changes
- **PR chat** — reply to a finding or mention `@revu` anywhere on the PR for a follow-up conversation
- **Review skills** — on-demand domain knowledge (security, framework patterns, etc.) loaded by the reviewer when relevant

## Run locally

Prerequisites: [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local),
[Docker](https://docs.docker.com/get-docker/) (optional, for OpenObserve).

Copy the template and fill in your credentials and AI keys. GitHub supports both PAT and
[GitHub App](docs/setup.md#github) auth (App recommended — comments post as `revu[bot]`):

```bash
cp Revu/local.settings.example.json Revu/local.settings.json
```

Configure AI credentials in [.NET user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
(shared by all test projects):

```bash
cd Revu
dotnet user-secrets set "Ai:Anthropic:ApiKey" "sk-ant-..."
```

Start the function host:

```bash
cd Revu
func start
```

To receive real ADO/GitHub webhooks locally, create a persistent dev tunnel:

```bash
devtunnel create revu --allow-anonymous
devtunnel port create revu -p 7071
devtunnel host revu
```

Then point your ADO service hooks at `{tunnel-url}/api/webhook/ado` (PR created/updated) and
`{tunnel-url}/api/webhook/ado/comment` (PR commented on). See [docs/setup.md](docs/setup.md)
for full deployment and webhook configuration.

Optional: start [OpenObserve](https://openobserve.ai/) for local observability (logs, traces, metrics):

```bash
docker run --rm -it -p 5080:5080 \
  -e ZO_ROOT_USER_EMAIL=root@example.com \
  -e ZO_ROOT_USER_PASSWORD=Complexpass#123 \
  --name openobserve openobserve/openobserve:latest
```

Dashboard at http://localhost:5080 (see [docs/observability.md](docs/observability.md)).

## Getting started

Revu only reviews registered repositories. Unregistered repos are silently ignored. To enroll a
repo, register it via `POST /api/manage/repos` (requires a function key), then create service
hooks for PR events. Optionally drop a `.revu.json` in the repo root to customize review behavior
(see [Configuration](#configuration)).

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
