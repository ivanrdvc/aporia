# Review Strategies

## Two Kinds of Strategy

`CoreStrategy` works remotely — it calls `IGitConnector` REST APIs via `ReviewerTools` to fetch
files, search code, and list directories. It never touches the filesystem.

`CopilotStrategy` (and future `ClaudeCodeStrategy`) work locally — they clone the repo and point
their agent at the checkout. The agent's built-in tools operate on the local filesystem directly.
No `ReviewerTools` needed.

| | CoreStrategy | CopilotStrategy | ClaudeCodeStrategy (future) |
|---|---|---|---|
| **LLM backend** | Any `IChatClient` (keyed) | GitHub Copilot CLI process | Claude Code CLI |
| **File access** | REST via `IGitConnector` | Local filesystem (built-in tools) | Local filesystem |
| **Structured output** | `ForJsonSchema<ReviewResult>()` | No — post-process extraction | TBD |
| **Tools** | `ReviewerTools` + explorers | Copilot's native read/search/shell | Claude Code's native tools |
| **Needs local clone** | No | Yes | Yes |
| **Host dependency** | None beyond .NET | `gh` CLI + Copilot extension | Claude Code CLI |

## RepoClone — Shared Infrastructure

`Aporia/Git/RepoClone.cs` — shallow-clones a repo to a temp directory, returns `Path`, deletes
everything on dispose. Used by any strategy that needs local access. See the
[copilot plan](2026-03-14-copilot-review-strategy.md#local-clone--shared-infrastructure) for full
security analysis (credential handling, repo content safety, disk space).

Key decisions:
- **Source branch, not target.** The reviewer needs the code as it will be after merge.
- **Shallow clone (`--depth 1`).** Tip commit only. ~15MB for a medium repo vs ~100MB full.
- **No sparse checkout.** Agents need the full tree to navigate freely.
- **`GIT_ASKPASS` for credentials.** Token never in process args (unlike `http.extraHeader`).
- **`IAsyncDisposable`.** `await using` guarantees cleanup. Orphans cleaned on container recycle.

## Structured Output Gap

CoreStrategy gets `ReviewResult` directly via JSON schema on the LLM response. Local-clone
strategies don't have this — Copilot SDK has no `ResponseFormat`, and CLI-based agents return
unstructured text.

Solution: post-process extraction. Run the raw response through a cheap `IChatClient` call
(`ModelKey.Default`) with `ForJsonSchema<ReviewResult>()`. Adds ~2s and ~3K tokens. Validated
in the Copilot spike — works reliably.

## Clone Credentials

Each provider has its own auth path for obtaining a clone token:

- **GitHub PAT mode:** Use `GitHubOptions.Token` directly.
- **GitHub App mode:** Get an installation token via `GitHubAuthHandler.GetInstallationTokenAsync()`
  using the `InstallationId` from the webhook payload.
- **ADO:** Use `AdoOrgConfig.PersonalAccessToken` from the matching organization.

`CopilotStrategy` takes `GitHubAuthHandler` as a dependency for App-mode token acquisition.
Future strategies follow the same pattern.

## Deployment Constraints

Local-clone strategies need their agent's CLI on the host. This is separate from `git` (which is
already in Azure Functions base images).

| Strategy | Extra host dependency | Auth requirement |
|---|---|---|
| Copilot | `gh` CLI + Copilot extension | `gh auth login` with Copilot entitlement |
| ClaudeCode | Claude Code CLI | API key or OAuth |

Both require a custom Docker image. The clone infrastructure itself has no extra dependencies.

## Open Questions

- **Skills gap.** CoreStrategy loads skills via `FileAgentSkillsProvider` + `AIContextProviders`.
  Copilot's `SessionConfig` doesn't support context providers — skills would need manual injection
  into the system message. Not blocking but limits review quality.
- **PR context gap.** Same issue — `PrContextProvider` can't be wired into Copilot agent.
  PR title/description/work items would need to be appended to the prompt.
- **Copilot model opacity.** Backend chooses the model. No control over reasoning effort or model
  selection. Review quality may differ from CoreStrategy.
- **Tool lockdown.** Copilot has shell/write tools. Currently approving all `Kind=read`
  permissions. Should enumerate built-in tool names and build an explicit allowlist.
