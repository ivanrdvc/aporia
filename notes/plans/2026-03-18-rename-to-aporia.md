---
date: 2026-03-18
status: draft
tags: [rename, branding]
---

# Rename Project from Revu to Aporia

## Problem Statement

The name "Aporia" is already taken by an existing TypeScript-based PR review tool. The project needs
a full rename to "Aporia" — a philosophy term meaning the productive moment of doubt in Socratic
examination. The rename spans the entire codebase: solution/project files, namespaces, config,
infrastructure, documentation, and connected apps (GitHub App, Azure resources).

## Decision Drivers

- Name collision with existing PR review tool makes the current name unusable
- Rename must be comprehensive — partial renames cause build failures and config mismatches
- Phased approach needed to keep the project buildable between phases
- Azure resources and GitHub App are live — renaming those has deployment implications
- The `.aporia.json` repo config file is a public contract for users

## Solution

Full rename executed in phases, ordered by dependency. Code and config first (can be done in one
session), infrastructure and external services last (require manual steps and coordination).

## Research Summary

**Scope inventory (all occurrences of "Aporia"/"revu"):**

| Category | Current | Target |
|----------|---------|--------|
| Solution file | `Aporia.slnx` | `Aporia.slnx` |
| Main project dir | `Aporia/` | `Aporia/` |
| Main project file | `Aporia/Aporia.csproj` | `Aporia/Aporia.csproj` |
| Test projects | `Revu.Tests.{Unit,Integration,Eval}/` | `Aporia.Tests.{Unit,Integration,Eval}/` |
| All C# namespaces | `Revu`, `Revu.*` | `Aporia`, `Aporia.*` |
| Options class | `AporiaOptions` | `AporiaOptions` |
| Config section | `"Aporia"` | `"Aporia"` |
| Env var prefix | `Aporia__` | `Aporia__` |
| Session keys | `revu:*` | `aporia:*` |
| Repo config file | `.aporia.json` / `.aporia.example.json` | `.aporia.json` / `.aporia.example.json` |
| Azure resources | `func-aporia`, `cosmos-aporia`, etc. | `func-aporia`, `cosmos-aporia`, etc. |
| Bot identity | `aporia[bot]` | `aporia[bot]` |
| GitHub workflows | references in `ci.yml`, `deploy.yml` | updated paths and resource names |
| Bicep infra | `param appName = 'revu'`, cosmos db name | updated to `'aporia'` |
| Launch settings | profile name `"Aporia"` | `"Aporia"` |
| VS Code tasks | build task references | updated paths |
| Documentation | README, CLAUDE.md, docs/*, notes/* | all references updated |

## Implementation Steps

### Phase 1 — Code rename (solution, projects, namespaces)

1. **Rename directories**
   - `Aporia/` → `Aporia/`
   - `tests/Aporia.Tests.Unit/` → `tests/Aporia.Tests.Unit/`
   - `tests/Aporia.Tests.Integration/` → `tests/Aporia.Tests.Integration/`
   - `tests/Aporia.Tests.Eval/` → `tests/Aporia.Tests.Eval/`

2. **Rename solution and project files**
   - `Aporia.slnx` → `Aporia.slnx`
   - `Aporia.csproj` → `Aporia.csproj`
   - `Aporia.Tests.Unit.csproj` → `Aporia.Tests.Unit.csproj`
   - `Aporia.Tests.Integration.csproj` → `Aporia.Tests.Integration.csproj`
   - `Aporia.Tests.Eval.csproj` → `Aporia.Tests.Eval.csproj`
   - Update project references and `InternalsVisibleTo` in all `.csproj` files

3. **Rename solution file contents**
   - Update `Aporia.slnx` to reference new project paths

4. **Rename all C# namespaces**
   - Global find/replace `namespace Revu` → `namespace Aporia` across all `.cs` files
   - Global find/replace `using Revu` → `using Aporia` across all `.cs` files
   - Rename `AporiaOptions` → `AporiaOptions` in `Infra/Options.cs` and all references

5. **Rename config keys in code**
   - Session keys: `revu:conversationId` → `aporia:conversationId`, etc.
   - Options section name: `"Aporia"` → `"Aporia"`

6. **Verify build**
   - `dotnet build Aporia.slnx`
   - `dotnet test tests/Aporia.Tests.Unit/Aporia.Tests.Unit.csproj`

### Phase 2 — Configuration and settings

7. **Rename config files**
   - `.aporia.example.json` → `.aporia.example.json`
   - Update all code references to `.aporia.json` → `.aporia.json`

8. **Update local settings**
   - `Aporia/local.settings.example.json`: `Aporia__*` → `Aporia__*`, OTEL service name
   - `Aporia/Properties/launchSettings.json`: profile name
   - Test `appsettings.test.json` files: any `Aporia__*` references

9. **Update VS Code config**
   - `.vscode/tasks.json`: project paths

### Phase 3 — Infrastructure

10. **Update Bicep**
    - `infra/main.bicep`: `param appName = 'aporia'`, cosmos db name, all `Aporia__` settings
    - Comment references to resource names

11. **Update GitHub Actions**
    - `.github/workflows/ci.yml`: test project path
    - `.github/workflows/deploy.yml`: publish path, `app-name`, `resource-group-name`

### Phase 4 — Documentation

12. **Update all markdown files**
    - `README.md`, `CLAUDE.md`, `CHANGELOG.md`, `TODO.md`
    - `docs/internals.md`, `docs/setup.md`, `docs/architecture.md`, `docs/observability.md`
    - `docs/features/*.md`
    - `notes/plans/*.md`, `notes/problems/*.md`
    - `tests/CLAUDE.md`, `tests/Aporia.Tests.Eval/CLAUDE.md`
    - Skills files in `.claude/skills/`

### Phase 5 — External services (manual)

13. **GitHub App**
    - Rename or create new GitHub App with name "aporia" / "aporia[bot]"
    - Update app ID, private key, webhook secret in Azure config

14. **Azure resources**
    - Decision: redeploy with new names vs. rename in place
    - Function app, Cosmos DB, resource group, Key Vault, App Insights
    - Update environment variables with `Aporia__` prefix

15. **Repository**
    - Rename GitHub repo from `revu` to `aporia`
    - Update all remote URLs

## Open Questions

- [ ] Should the repo root directory also rename from `revu/` to `aporia/`? (local dev concern)
- [ ] Redeploy Azure resources with new names, or rename existing ones?
- [ ] Create a new GitHub App named "aporia" or rename the existing one?
- [ ] Should `.aporia.json` support be kept temporarily for backward compat, or clean break?
- [ ] Update Claude memory files (`/Users/ivan/.claude/projects/`) — these reference "revu" in paths
