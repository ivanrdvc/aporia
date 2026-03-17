---
date: 2026-03-14
status: draft
tags: [copilot, review-strategy, github-copilot, maf]
---

# Add CopilotStrategy via GitHub Copilot SDK

## Problem Statement

Revu has a strategy abstraction (`IReviewStrategy`) with `CoreStrategy` as the only implementation.
The constant `ReviewStrategy.Copilot = "copilot"` is already declared but has no backing class.
GitHub Copilot is now available as a programmable agent via the `GitHub.Copilot.SDK` NuGet package,
and the Microsoft Agent Framework (MAF) provides a ready-made adapter
(`Microsoft.Agents.AI.GitHub.Copilot`) that bridges the Copilot SDK to the `AIAgent` interface
Revu already uses. Adding a `CopilotStrategy` would let repos opt into Copilot-powered reviews
via `.revu.json` config (`"strategy": "copilot"`).

## Decision Drivers

- **Same interface, different runtime.** `CopilotStrategy` must implement `IReviewStrategy` and
  return a `ReviewResult` — the Reviewer doesn't care which strategy produced the findings.
- **No structured output.** The Copilot SDK has no `ResponseFormat` or JSON schema support. The
  strategy must parse `ReviewResult` from unstructured text (prompt-based JSON extraction or a
  post-processing step).
- **Copilot has its own tools.** With a `WorkingDirectory` set, Copilot uses its native
  file/search/navigation tools on the local filesystem. No need for `ReviewerTools` — those
  exist because `CoreStrategy` has no local access and calls `IGitConnector` REST APIs instead.
- **Model is opaque.** The Copilot backend chooses the model (likely GPT-4o). No control over
  model selection, reasoning effort, or token limits from the caller's side. `SessionConfig.Model`
  exists but may be ignored by the backend.
- **Local CLI dependency.** `CopilotClient` launches the GitHub Copilot CLI locally — it's not a
  REST API call. The Azure Functions host must have the Copilot CLI installed and authenticated.
  This is a significant deployment constraint.
- **Reuse MAF adapter.** `GitHubCopilotAgent` already handles session management, streaming,
  event conversion, and tool dispatch. No need to interact with `CopilotClient` directly.

## Solution

Add `CopilotStrategy : IReviewStrategy` in `Revu/Review/CopilotStrategy.cs`. It clones the repo
via shared `RepoClone` infrastructure (`Revu/Git/RepoClone.cs`), creates a `CopilotClient`,
wraps it as an `AIAgent` via MAF's `AsAIAgent()` extension with `WorkingDirectory` pointing at
the clone, sends the review prompt, and parses `ReviewResult` from the response text.

Copilot uses its own native tools for file reading, code search, and directory navigation —
no `ReviewerTools` needed. The same `RepoClone` infrastructure will be reused by
`ClaudeCodeStrategy` (future), which also operates on a local working directory.

```csharp
public class CopilotStrategy(IOptions<GitHubOptions> ghOptions, ILogger<CopilotStrategy> logger) : IReviewStrategy
{
    public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config,
        PrContext prContext, CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
    {
        var orgConfig = ghOptions.Value.Organizations[req.Organization];
        string? cloneDir = null;
        try
        {
            cloneDir = await CloneAsync(req, orgConfig, ct);
            await using var client = new CopilotClient(new CopilotClientOptions { AutoStart = false });

            var agent = client.AsAIAgent(
                sessionConfig: new SessionConfig
                {
                    WorkingDirectory = cloneDir,
                    SystemMessage = new() { Mode = SystemMessageMode.Append, Content = Prompts.ReviewerInstructions },
                    OnPermissionRequest = (_, _) => Task.FromResult(PermissionResponse.Deny),
                });

            var prompt = CoreStrategy.BuildReviewPrompt(diff);
            var session = await agent.CreateSessionAsync(ct);
            var response = await agent.RunAsync(prompt, session, cancellationToken: ct);

            return ParseReviewResult(response);
        }
        finally
        {
            TryDeleteDir(cloneDir);
        }
    }
}
```

Key difference from `CoreStrategy`: no structured output guarantee, so the response must be
parsed from text. Two approaches:

**Option A — Prompt-based JSON.** Append "respond with JSON matching this schema" to the prompt
with the `ReviewResult` schema inlined. Parse from the response text. Fragile but simple.

**Option B — Post-processing extraction.** Run the Copilot response through a second, cheap
`IChatClient` call (e.g. `ModelKey.Default`) with structured output enabled to extract
`ReviewResult` from the natural-language response. Reliable but adds latency and cost.

## Research Summary

### MAF GitHub Copilot Package

- **NuGet:** `Microsoft.Agents.AI.GitHub.Copilot` (wraps `GitHub.Copilot.SDK` v0.1.29)
- **Target:** .NET 8.0+ (Revu is .NET 10, compatible)
- **Key class:** `GitHubCopilotAgent : AIAgent` — sealed, handles session lifecycle, streaming,
  event conversion, tool dispatch via `SessionConfig.Tools`
- **Extension:** `CopilotClient.AsAIAgent()` — bridges SDK client to MAF agent interface
- **Source:** `/Users/ivan/dev/agent-framework/dotnet/src/Microsoft.Agents.AI.GitHub.Copilot/`

### SessionConfig Capabilities

| Feature | Supported | Notes |
|---------|-----------|-------|
| Tools (`List<AIFunction>`) | Yes | Same `AIFunction` type used by CoreStrategy |
| System message | Yes | `SystemMessageConfig` with Mode (Append) and Content |
| Model selection | Partial | `SessionConfig.Model` exists but backend may ignore |
| Reasoning effort | Partial | `SessionConfig.ReasoningEffort` exists, may be ignored |
| Structured output / ResponseFormat | **No** | No JSON schema support — must parse from text |
| Streaming | Yes | Default behavior, converts to `AgentResponseUpdate` |
| MCP servers | Yes | `SessionConfig.McpServers` for external tool servers |

### CopilotClient Runtime

- **Not a REST API.** Launches the GitHub Copilot CLI as a local process.
- **State lifecycle:** `StartAsync()` → `CreateSessionAsync()` → `SendAsync()` → `DisposeAsync()`
- **Event-driven:** Session emits `AssistantMessageDeltaEvent`, `AssistantMessageEvent`,
  `SessionIdleEvent`, `SessionErrorEvent` via `copilotSession.On()`.
- **Auth:** Requires `gh auth login` with Copilot entitlement on the host machine.

### Existing Strategy Pattern (`Revu/Review/`)

- `IReviewStrategy.Review()` signature: `(ReviewRequest, Diff, ProjectConfig, PrContext,
  CodeGraphQuery?, CancellationToken)` → `Task<ReviewResult>`
- `CoreStrategy` uses `IChatClient.AsAIAgent()` with `ChatClientAgentOptions` including
  `ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewResult>()`
- Strategy resolved by key via `Func<string, IReviewStrategy>` factory in `Reviewer`
- DI registration: `AddKeyedScoped<IReviewStrategy, CoreStrategy>(ReviewStrategy.Core)`
- `ReviewerTools` is instantiated per-review inside the strategy — not a DI dependency
- `CoreStrategy.BuildReviewPrompt()` is `internal static` — reusable by other strategies

### No Existing Samples

MAF has samples for OpenAI, Anthropic, Foundry, etc. but no Copilot-specific sample showing
tool use + structured extraction. The integration tests in
`Microsoft.Agents.AI.GitHub.Copilot.IntegrationTests` show basic session creation and message
exchange only.

## Implementation Steps

### Phase 1 — Ready now (no blockers)

1. **Add `RepoClone` shared infrastructure**
   - Files: `Revu/Git/RepoClone.cs` (new)
   - `IAsyncDisposable` class — shallow-clones a repo, exposes `Path`, deletes on dispose.
   - Caller-agnostic: takes `cloneUrl`, `branch`, `token`. No strategy-specific logic.
   - Security: `GIT_ASKPASS`-based auth, symlinks off, LFS off, filters off, no submodules.
   - Reused by `CopilotStrategy` now and `ClaudeCodeStrategy` later.
   - Self-contained — no external NuGet dependencies beyond `git` CLI.

2. **Add NuGet references**
   - Files: `Revu/Revu.csproj`
   - Add `GitHub.Copilot.SDK` (pin v0.1.29) and `Microsoft.Agents.AI.GitHub.Copilot`.
   - Verify version compatibility with .NET 10.

3. **Create CopilotStrategy skeleton + DI registration**
   - Files: `Revu/Review/CopilotStrategy.cs` (new), `Revu/Program.cs`
   - Implement `IReviewStrategy`. Constructor takes `IOptions<GitHubOptions>`,
     `ILogger<CopilotStrategy>`.
   - In `Review()`: clone via `RepoClone.CreateAsync()`, create `CopilotClient` with
     `WorkingDirectory = clone.Path`, call `AsAIAgent()` with instructions, send prompt via
     `RunAsync()`, parse `ReviewResult` from response. `await using` handles cleanup.
   - Copilot uses its own native file/search tools on the local clone — no `ReviewerTools`.
   - Reuse `CoreStrategy.BuildReviewPrompt()` for the diff prompt.
   - Lock down permissions: `OnPermissionRequest` denies all.
   - DI: `builder.Services.AddKeyedScoped<IReviewStrategy, CopilotStrategy>(ReviewStrategy.Copilot);`
   - No other DI changes — `Reviewer` already resolves strategies by key.
   - Leave TODO markers for: parsing approach, `ExcludedTools` lockdown.

### Phase 2 — Blocked / needs spike

4. **Handle missing structured output** _(blocked: needs spike on both options)_
   - **Option A — Prompt-based JSON.** Append schema to prompt. Fragile but simple.
   - **Option B — Post-processing extraction (recommended).** Run response through a second
     `IChatClient` call (`ModelKey.Default`) with structured output to extract `ReviewResult`.
     Reliable but adds latency and cost. Preferred because `IChatClient` with structured output
     already works in `CoreStrategy` — the extraction call is cheap and predictable.
   - Either way, add robust fallback when parsing fails (return empty findings with error summary).
   - If Option B: inject `[FromKeyedServices(ModelKey.Default)] IChatClient` for post-processing.

5. **Tool lockdown** _(blocked: need to enumerate Copilot built-in tool names)_
   - `ExcludedTools` / `AvailableTools` requires knowing the exact tool names exposed by the
     Copilot CLI. Need to create a session and inspect the tool list.

6. **Custom Docker image** _(blocked: deployment feasibility unknown)_
   - Files: `Dockerfile` (new or updated)
   - `CopilotStrategy` requires `gh` CLI + `gh copilot` extension + `GH_TOKEN` env var.
   - `ClaudeCodeStrategy` (future) will require Claude Code CLI instead.
   - `git` CLI is already in the Azure Functions base images — no extra dependency for cloning.
   - Key question: can the Copilot CLI (a local process) run in an Azure Functions container?

## Open Questions

- [ ] **Copilot CLI in Azure Functions.** The SDK launches a local CLI process. Can this work
  in an Azure Functions container? May need a custom Docker image with the Copilot CLI installed
  and authenticated. This could be a blocker for cloud deployment.
- [ ] **Structured output parsing.** Option A (prompt-based JSON) vs Option B (post-processing
  via IChatClient). Option B is more reliable but adds latency/cost. Need to spike both.
- [ ] **Model quality.** The Copilot backend model is opaque. Review quality may be lower than
  CoreStrategy's reasoning model. Need to run the same test PR through both strategies and
  compare.
- [ ] **Copilot licensing.** Does the GitHub Copilot SDK require a Copilot Business/Enterprise
  license? Or does individual Copilot Pro suffice? Need to verify entitlement requirements.
- [ ] **Rate limits / costs.** Copilot SDK usage may be metered differently from direct API
  calls. Need to understand the billing model.
- [ ] **SessionConfig.Model behavior.** Can we actually select a model (e.g. claude-opus-4.5,
  gpt-4o) via the config, or does the backend ignore it? Test with different values.
- [ ] **Skills and context providers.** CoreStrategy uses `FileAgentSkillsProvider` and
  `PrContextProvider` via `AIContextProviders`. The Copilot agent doesn't support
  `ChatClientAgentOptions` / `AIContextProviders` — skills would need to be loaded manually
  and injected into the system message or as tool results.
- [ ] **Code graph access.** Copilot's native tools don't know about Revu's code graph index.
  Options: inject `QueryCodeGraph` as an extra `SessionConfig.Tools` entry, or skip it (Copilot
  can grep/read files directly). Need to verify mixed tool sources work if injecting.
- [ ] **Copilot ExcludedTools / AvailableTools names.** Need to enumerate the exact built-in
  tool names in the Copilot CLI to build the read-only allowlist/denylist.
- [ ] **GH_TOKEN scope for Copilot CLI.** Verify whether the Copilot CLI auth token needs
  special scopes beyond `copilot`, and whether it can be a GitHub App token or must be a PAT.

## Local Clone — Shared Infrastructure

Both `CopilotStrategy` and `ClaudeCodeStrategy` (future) need a local clone — they operate on
the filesystem, not via REST APIs. The clone logic lives in `Revu/Git/RepoClone.cs` as shared
infrastructure, not inside any individual strategy.

`CoreStrategy` doesn't need this — it uses `ReviewerTools` which call `IGitConnector` REST APIs.

### RepoClone — `Revu/Git/RepoClone.cs`

`IAsyncDisposable` wrapper. Clones on creation, deletes on dispose. Strategies get a `Path`
and set it as their agent's working directory.

```csharp
public sealed class RepoClone : IAsyncDisposable
{
    public string Path { get; }
    private readonly string? _askPassScript;

    private RepoClone(string path, string? askPassScript)
    {
        Path = path;
        _askPassScript = askPassScript;
    }

    public static async Task<RepoClone> CreateAsync(
        string cloneUrl, string branch, string token, CancellationToken ct = default)
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"revu_{Guid.NewGuid():N}");

        // GIT_ASKPASS: token never appears in process arguments or /proc/PID/cmdline.
        // The script prints the token to stdout when git calls it for credentials.
        var askPassScript = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"revu_askpass_{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(askPassScript, $"#!/bin/sh\necho '{token}'", ct);
        File.SetUnixFileMode(askPassScript, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            ArgumentList =
            {
                // Repo content safety
                "-c", "core.symlinks=false",
                "-c", "core.hooksPath=/dev/null",
                "-c", "core.fsmonitor=false",
                "-c", "filter.lfs.process=",
                "-c", "filter.clean=",
                "-c", "filter.smudge=",
                "-c", "protocol.file.allow=never",
                // Clone flags
                "clone",
                "--depth", "1",
                "--branch", branch,
                "--single-branch",
                "--no-recurse-submodules",
                "--no-tags",
                cloneUrl,
                tempDir
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.Environment["GIT_ASKPASS"] = askPassScript;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            TryDelete(tempDir);
            TryDelete(askPassScript);
            throw new InvalidOperationException($"git clone failed (exit {process.ExitCode}): {stderr}");
        }

        return new RepoClone(tempDir, askPassScript);
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(_askPassScript);
        TryDelete(Path);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best effort — container recycle handles orphans */ }
    }
}
```

**Usage from any strategy:**

```csharp
await using var clone = await RepoClone.CreateAsync(
    $"https://github.com/{orgConfig.Owner}/{req.RepositoryName}.git",
    req.SourceBranch,
    orgConfig.Token,
    ct);

// CopilotStrategy:  SessionConfig.WorkingDirectory = clone.Path
// ClaudeCodeStrategy: pass clone.Path as working directory to Claude Code CLI
```

### What It Clones

Shallow clone of the PR source branch — all files, no history.

- `--depth 1` — tip commit only. Cuts size dramatically (e.g. dotnet/eShop: ~100MB full →
  ~15MB shallow).
- `--single-branch` — only the PR source branch, no other refs.
- `--no-recurse-submodules` — blocks submodule pulls (security + size).
- Source branch (not target) because the reviewer needs the code as it will be after merge —
  same ref `IGitConnector.GetFile` uses today (`req.SourceBranch`).
- No sparse checkout — agents need the full tree to navigate freely (find usages, related
  files, architectural patterns).

### Credential Security

The token is passed via `GIT_ASKPASS` — a temporary script that prints the token to stdout
when git requests credentials. This is the same approach Jenkins uses. The token never appears
in process arguments, so it's not visible via `/proc/PID/cmdline` on Linux (unlike
`http.extraHeader` via `-c`, which exposes the token in the process's argument list to any
process on the host).

The askpass script is created with `0500` permissions (owner read+execute only) and deleted
alongside the clone directory in `DisposeAsync()`.

Three layers prevent credential leakage:

| Vector | Protection |
|--------|------------|
| `.git/config` persistence | `GIT_ASKPASS` is env-only — git doesn't write askpass paths to config |
| `/proc/PID/cmdline` exposure | Token is in a temp file, not in process args |
| URL-embedded token | Clone URL has no credentials — `https://github.com/...` |
| System/global git config | `GIT_CONFIG_NOSYSTEM=1` + `GIT_CONFIG_GLOBAL=/dev/null` |
| Shell injection | `ArgumentList` (not `Arguments`) + `UseShellExecute = false` |

### Repo Content Safety

A cloned repo is untrusted input. The flags mitigate known git attack vectors:

| Threat | Mitigation |
|--------|------------|
| `.gitattributes` filter execution | `-c filter.clean= -c filter.smudge= -c filter.lfs.process=` blanks all filter drivers |
| Submodule pulls from arbitrary URLs | `--no-recurse-submodules` (also blocks CVE-2025-48384) |
| Symlinks outside repo (e.g. `/etc/passwd`) | `-c core.symlinks=false` materializes as plain files |
| Post-checkout / post-merge hooks | `-c core.hooksPath=/dev/null` — no hooks can execute |
| fsmonitor code execution | `-c core.fsmonitor=false` |
| Local file protocol abuse | `-c protocol.file.allow=never` |
| LFS exhausting disk | `GIT_LFS_SKIP_SMUDGE=1` |
| Path traversal (`../../etc/shadow`) | Git rejects these on modern versions |

**Git version requirement:** Ensure container image has Git >= 2.48.2 for CVE-2025-48384 patch
(critical RCE via `.gitmodules` carriage return injection, added to CISA KEV).

### Lifecycle and Crash Recovery

`IAsyncDisposable` + `await using` guarantees cleanup on success or failure. If the process
crashes mid-review, the temp directory is orphaned. Mitigations:

- Container filesystem is ephemeral — orphans cleaned up on container recycle.
- Optional startup sweep: scan `revu_*` dirs in temp, delete any older than 1 hour.
- At ~15MB per shallow clone, even several orphans are negligible.

Concurrency: each review gets a GUID-named temp directory. No conflicts.

### Disk Space

Shallow clone of a medium repo: ~15MB. At 5 concurrent reviews: ~75MB.

| Plan | Temp storage | Feasible? |
|------|-------------|-----------|
| Consumption | ~500MB | Yes for medium repos, tight for monorepos |
| Premium / Dedicated | ~1GB+ | Fine |
| Container Apps | Configurable | Best option |

### Hosting

Clone requires `git` CLI on the host. The Azure Functions base images (both Linux and Windows)
include `git`. No custom Docker needed for cloning alone.

`CopilotStrategy` additionally needs `gh` CLI + Copilot extension + auth — that's what forces
the custom Docker image. `ClaudeCodeStrategy` would need the Claude Code CLI instead. The
clone infrastructure itself has no extra host dependencies.


## Notes for Implementation

- `GitHub.Copilot.SDK` is at v0.1.29 — pre-release. API surface may change. Pin the version.
- The `CopilotClient` must be created and disposed per review (it's a process). Don't try to
  make it a singleton.
- `CoreStrategy.BuildReviewPrompt()` is already `internal static` — call it directly from
  `CopilotStrategy`, no refactoring needed.
- The Copilot agent's `SessionConfig.SystemMessage` uses `SystemMessageMode.Append` — it
  appends to whatever system message the Copilot backend already has. This means
  `Prompts.ReviewerInstructions` will be additive, not a full replacement. The base Copilot
  system prompt may interfere with review behavior. Watch for this.
- Consider making this strategy opt-in and experimental. Gate behind a feature flag or config
  value so it doesn't affect production ADO reviews.
- **Signature update (2026-03-17):** `IReviewStrategy.Review()` now takes `PrContext prContext`
  instead of `IGitConnector git`. The plan's code snippets have been updated. `CopilotStrategy`
  receives `PrContext` but doesn't need `IGitConnector` directly — it operates on a local clone
  rather than REST APIs.
- **Skills and context providers:** `CoreStrategy` uses `FileAgentSkillsProvider` and
  `PrContextProvider` via MAF's `AIContextProviders`. The Copilot agent uses `SessionConfig`
  (not `ChatClientAgentOptions`), so these providers can't be wired in directly. Skills would
  need to be loaded manually and injected into the system message. This is a Phase 2 concern —
  the skeleton can work without skills initially.
