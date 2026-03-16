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

## Code Search behavior (ADO)

ADO Code Search (`almsearch.dev.azure.com`) tokenizes queries and ANDs the tokens, so a multi-word query like `var order = await` matches any file containing all those tokens, returning broad noise. Identifiers are atomic: `SetAwaitingValidation` returns nothing even though `SetAwaitingValidationOrderStatusCommandHandler` exists; suffix wildcards (`SetAwaitingValidation*`) are required for partial matches. Exact phrase matching works with quotes (`"return true"`), and boolean `OR` is supported but unreliable for code search.

# GitHub Connector

`GitHubConnector` implements `IGitConnector` using the GitHub REST API v3.

## GetDiff flow

GitHub provides unified patches directly via the PR files endpoint — no local diffing needed
(unlike ADO). File content is fetched via the contents API; files over 1MB fall back to the
blob API.

Incremental reviews use the compare API (`base...head`) with a 250-file cap. If the result
is truncated or the cursor SHA is gone (force-push), falls back to full PR file list.

## PostReview

Comments are posted as pull request reviews (inline) + an issue comment (summary). Key
differences from ADO:

- **Dedup**: HTML comment fingerprints (`<!-- revu:fp:hash -->`) in comment bodies, matched
  via regex on existing review comments. ADO uses thread properties.
- **Line placement**: comments must target lines within diff hunks. Lines outside hunks go
  into the review body as a text list.
- **Retry chain**: GitHub returns 422 for invalid comment positions. Fallback sequence:
  batch → individual → strip suggestion → single-line → skip.
- **Summary**: upserted as an issue comment with a marker (`<!-- revu:summary -->`).

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

# Local testing

Queue names are configurable via `%ReviewQueue%`, `%ChatQueue%`, `%IndexQueue%` app settings — used by both triggers (consumers) and output bindings (producers). Prod uses `review-queue` / `chat-queue` / `index-queue`; local uses `*-dev` variants so the two never compete for messages.

To test with real ADO webhooks locally: run `devtunnel host -p 7071 --allow-anonymous`, create an ADO service hook for `Pull request commented on` pointing at `{tunnel-url}/api/webhook/ado/comment`, then `func start` — comments with `@revu` on any registered repo will flow through the local pipeline.
