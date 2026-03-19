---
date: 2026-03-19
status: draft
tags: [strategy, claude-code, review]
---

# ClaudeCodeStrategy via IChatClient wrapper

## Problem Statement

Aporia needs a third review strategy that runs Claude Code CLI against a local repo clone. The
current CopilotStrategy uses `GitHubCopilotAgent` (the Copilot SDK's MAF adapter) directly, which
bypasses the MEAI middleware pipeline — no turn limits, no OTel, and requires a post-process LLM
call to extract structured output. Claude Code CLI natively supports structured output
(`--json-schema`), turn limits (`--max-turns`), tool control (`--allowedTools`), and streaming
NDJSON (`--output-format stream-json`), making the `IChatClient` wrapper approach a natural fit.

## Decision Drivers

- Claude Code CLI has no C# SDK — must spawn as subprocess (same as official TS/Python SDKs do)
- CLI natively supports `--json-schema` for structured output, eliminating the extraction LLM call
- CLI supports `--max-turns` and `--system-prompt`, giving direct control CopilotStrategy lacks
- Wrapping as `IChatClient` plugs into the existing MEAI pipeline (OTel, logging, turn limits)
  and converges with CoreStrategy's architecture
- `--output-format stream-json` emits NDJSON events that map to `GetStreamingResponseAsync`
- `RepoClone` infrastructure already exists and is shared across local-clone strategies

## Solution

Build `ClaudeCodeChatClient : IChatClient` that spawns `claude -p --output-format stream-json` as
a child process. Register it as a keyed `IChatClient`, then use MAF's `AsAIAgent()` to get a
`ChatClientAgent` — the same shape as CoreStrategy. No post-process extraction needed.

Architecture convergence:

```
CoreStrategy:       IChatClient (remote API)     → MEAI pipeline → ChatClientAgent
ClaudeCodeStrategy: IChatClient (CLI subprocess) → MEAI pipeline → ChatClientAgent
CopilotStrategy:    GitHubCopilotAgent (SDK-provided, different shape)
```

## Research Summary

### Claude Agent SDK (TS/Python — no C# SDK exists)

The official SDKs spawn the Claude Code binary as a subprocess and communicate via stdin/stdout
JSON. The `query()` function accepts options and returns an async iterator of `SDKMessage` events.

**Key CLI flags for headless operation:**

| Flag | Purpose |
|------|---------|
| `-p` / `--print` | Non-interactive mode |
| `--output-format stream-json` | NDJSON event stream on stdout |
| `--json-schema '...'` | Structured output — validated JSON in final result message |
| `--system-prompt` | Full system prompt (or `--append-system-prompt` to extend default) |
| `--allowedTools Read Glob Grep` | Pre-approved tools (no permission prompts) |
| `--disallowedTools Edit Bash` | Blocked tools |
| `--max-turns N` | Turn limit |
| `--cwd /path` | Working directory |
| `--model claude-sonnet-4-6` | Model selection |
| `--permission-mode dontAsk` | Deny anything not in allowedTools |

**NDJSON message types (stdout):**

- `SDKSystemMessage` (type: `"system"`, subtype: `"init"`) — session ID
- `SDKAssistantMessage` (type: `"assistant"`) — contains Anthropic API `BetaMessage` with
  content blocks (text, tool_use)
- `SDKUserMessage` (type: `"user"`) — tool results
- `SDKResultMessage` (type: `"result"`) — final output with `result` text,
  `structured_output` (if schema given), `duration_ms`

### CodexSharpSDK pattern (reference implementation)

`managedcode/CodexSharpSDK` wraps the Codex CLI as `IChatClient` with this layering:

1. **`CodexChatClient : IChatClient`** — spawns CLI, maps `ChatMessage` to prompt, maps results
   back to `ChatResponse`. Custom `AIContent` subclasses for CLI-specific data (file changes,
   commands).
2. **Internal mappers** — `ChatMessageMapper` (message flattening), `ChatOptionsMapper` (options
   bridge), `ChatResponseMapper` (result → ChatResponse), `StreamingEventMapper` (events →
   ChatResponseUpdate).
3. **DI extension** — `AddCodexChatClient()` registers as singleton `IChatClient`.
4. **AgentFramework bridge** — separate project calls `chatClient.AsAIAgent()` to get a
   `ChatClientAgent` for MAF.

### Comparison: Copilot SDK vs Claude Code CLI

| Concern | Copilot SDK | Claude Code CLI |
|---------|-------------|-----------------|
| Structured output | Not supported → extraction LLM call | `--json-schema` → direct |
| Turn limit | Not exposed → manual timeout | `--max-turns` |
| Tool control | Permission callback (string matching) | `--allowedTools` / `--disallowedTools` |
| System prompt | Append-only via `SessionConfig` | Full replace or append |
| Streaming | Events via MAF adapter | NDJSON on stdout |
| Model control | Opaque | `--model` flag |

## Implementation Steps

1. **Add `ClaudeCodeChatClient : IChatClient`**
   - File: `Aporia/Review/ClaudeCode/ClaudeCodeChatClient.cs`
   - Spawn `claude -p --output-format stream-json --json-schema <schema> --cwd <clone> --system-prompt <prompt> --allowedTools Read Glob Grep --permission-mode dontAsk --max-turns 15`
   - `GetResponseAsync`: start process, read all NDJSON lines, find `SDKResultMessage`, extract
     `structured_output` as `ReviewResult`. Map token usage from result message.
   - `GetStreamingResponseAsync`: yield `ChatResponseUpdate` for each `SDKAssistantMessage` text
     content block. Return final structured output on `SDKResultMessage`.
   - Kill process tree on cancellation (same pattern as `RepoClone`).

2. **Add NDJSON message types**
   - File: `Aporia/Review/ClaudeCode/SdkMessages.cs`
   - Minimal records for `SDKSystemMessage`, `SDKAssistantMessage`, `SDKResultMessage` with
     `System.Text.Json` deserialization. Only model what we read — don't mirror the full SDK.

3. **Add `ClaudeCodeStrategy : IReviewStrategy`**
   - File: `Aporia/Review/ClaudeCode/ClaudeCodeStrategy.cs`
   - Clone repo via `RepoClone.CreateAsync` (shared infrastructure).
   - Build system prompt: `Prompts.ReviewerInstructions` + `PrContextProvider.BuildInstructions`
     (same pattern as CopilotStrategy after the PrContext fix).
   - Create `ClaudeCodeChatClient` pointed at clone path.
   - Wrap with MEAI pipeline (`UseFunctionInvocation`, `UseOpenTelemetry`) and `AsAIAgent()`.
   - Run agent, return `ReviewResult` directly from structured output — no extraction step.

4. **Register in DI**
   - File: `Aporia/Program.cs`
   - `builder.Services.AddKeyedScoped<IReviewStrategy, ClaudeCodeStrategy>(ReviewStrategy.ClaudeCode);`

5. **Update integration test fixture**
   - File: `tests/Aporia.Tests.Integration/Fixtures/AppFixture.cs`
   - Register `ClaudeCodeStrategy` keyed service.
   - `TestTarget:Strategy=claude-code` env var override (same pattern as Copilot).

## Open Questions

- [ ] Should `ClaudeCodeChatClient` be a general-purpose reusable `IChatClient` (like
      CodexSharpSDK) or a minimal internal class scoped to the review use case?
- [ ] Which Claude model to default to? `claude-sonnet-4-6` for cost, `claude-opus-4-6` for
      quality? Should it be configurable via `ProjectConfig`?
- [ ] `--max-turns` value — needs tuning. CoreStrategy uses `MaximumIterationsPerRequest = 40`
      (MAF default). Claude Code turns are heavier (multi-tool per turn), so 10-15 may suffice.
- [ ] Host dependency: `claude` CLI must be installed on the Azure Functions host. Dockerfile
      change needed. Version pinning strategy?
