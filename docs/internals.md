# Structured output

MEAI's OpenAI adapter only enables constrained decoding when `ChatOptions.AdditionalProperties["strict"] = true`. Without it, `ForJsonSchema<T>()` sends the schema as a non-binding hint and the model can produce malformed output. Anthropic's API always constrains when a schema is provided (no `strict` flag), but its SDK's `IChatClient` silently ignores `ResponseFormat`, so `AnthropicOptionsAdapter` bridges the gap via `RawRepresentationFactory`. MAF's `ChatClientAgent` preserves `ResponseFormat` through `CreateConfiguredChatOptions`. The agent layer is not the problem.

# Azure DevOps Connector

`AdoConnector` implements `IGitConnector`, dumb transport only. It fetches raw data and delegates all logic elsewhere.

## Why GetDiff fetches two versions

ADO has no API that returns a unified diff. `GetFileDiffsAsync` returns line number ranges only, no content. There is an open feature request for patch/diff output; GitHub supports it natively (`Accept: application/vnd.github.diff`).

The workaround: fetch both the base and target file content, then diff locally with DiffPlex. `DiffBuilder` handles the hunk construction; `AdoConnector` only coordinates the two fetches.

## GetDiff flow

1. `GetPullRequestIterationsAsync` + `GetPullRequestIterationChangesAsync` → file list for the last iteration
2. For each changed file that passes `ShouldInclude`:
   - Fetch target content from the source iteration commit
   - For `Edit`: fetch base content from the target iteration commit → `DiffBuilder.Hunks`
   - For `Add`: no base exists → `DiffBuilder.NewFile` (all lines marked `+`)
   - For `Rename`: include `OldPath`; if rename+edit, diff old path at target commit vs new path at source commit
3. Return `Diff { Files: FileChange[] }` where `Content` is the diff string

The file list comes from the iteration API (PR-scoped, handles renames). Content is pinned to that iteration's source/target commit SHAs, so diffs are stable even if branch tips move while the review is running.

## Code Search behavior

ADO Code Search (`almsearch.dev.azure.com`) tokenizes queries and ANDs the tokens, so a multi-word query like `var order = await` matches any file containing all those tokens, returning broad noise. Identifiers are atomic: `SetAwaitingValidation` returns nothing even though `SetAwaitingValidationOrderStatusCommandHandler` exists; suffix wildcards (`SetAwaitingValidation*`) are required for partial matches. Exact phrase matching works with quotes (`"return true"`), and boolean `OR` is supported but unreliable for code search.

# Cosmos DB

Single database (`revu`), one Cosmos account. All stores are singletons that take `CosmosDb` (shared `CosmosClient` wrapper in `Infra/Cosmos/`). Each store owns a private `Document` class for serialization and exposes a clean public record.

## Containers

| Container | Partition Key | TTL | Store class |
|---|---|---|---|
| `repositories` | `/id` | none | `RepoStore` (`Infra/Cosmos/`) |
| `pr-state` | `/repositoryId` | 90 days | `PrStateStore` (`Infra/Cosmos/`) |
| `reviews` | `/repositoryId` | none | `ReviewStore` (`Infra/Cosmos/`) |
| `sessions` | `/conversationId` | 180 days | `CosmosChatHistoryProvider` (MAF built-in) |

**repositories** — one document per registered repo. Gates webhooks (unregistered repos ignored). Holds provider, enabled flag, name, URL, and `lastReviewedAt` (denormalized from latest review).

**pr-state** — one document per PR, keyed `{repositoryId}-pr-{pullRequestId}`. Tracks last reviewed iteration for incremental diffs. 90-day TTL since merged PRs don't need tracking.

**reviews** — one document per review event. Captures status, findings count by severity, token usage, duration, and `conversationId` link to the AI session. Foundation for history and metrics.

**sessions** — AI conversation history. Managed by MAF's `CosmosChatHistoryProvider`. 180-day TTL. Linked from review documents for debugging.
