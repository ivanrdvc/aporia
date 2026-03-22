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

## Authentication

`GitHubAuthHandler` (`DelegatingHandler`) handles auth transparently in the HttpClient pipeline.
**PAT mode**: static `Bearer <token>`. **App mode**: signs a JWT with the App's private key,
exchanges it for a short-lived installation token (cached 55min), sets that as Bearer. The
`InstallationId` comes from the webhook payload via `HttpRequestMessage.Options` — one App key
serves all installations. Mode auto-selected based on whether `AppId`/`PrivateKey` are configured.

## GetDiff flow

GitHub provides unified patches directly via the PR files endpoint — no local diffing needed
(unlike ADO). File content is fetched via the contents API; files over 1MB fall back to the
blob API.

Incremental reviews use the compare API (`base...head`) with a 250-file cap. If the result
is truncated or the cursor SHA is gone (force-push), falls back to full PR file list.

## PostReview

Comments are posted as pull request reviews (inline) + an issue comment (summary). Key
differences from ADO:

- **Dedup**: HTML comment fingerprints (`<!-- aporia:fp:hash -->`) in comment bodies, matched
  via regex on existing review comments. ADO uses thread properties.
- **Line placement**: comments must target lines within diff hunks. Lines outside hunks go
  into the review body as a text list.
- **Retry chain**: GitHub returns 422 for invalid comment positions. Fallback sequence:
  batch → individual → strip suggestion → single-line → skip.
- **Summary**: upserted as an issue comment with a marker (`<!-- aporia:summary -->`).

# Cosmos DB

Single database (`aporia`), one Cosmos account. All stores are singletons that take `CosmosDb` (shared `CosmosClient` wrapper in `Infra/Cosmos/`). Each store owns a private `Document` class for serialization and exposes a clean public record.

## Containers

| Container | Partition Key | TTL | Store class |
|---|---|---|---|
| `repositories` | `/id` | none | `RepoStore` (`Infra/Cosmos/`) |
| `pr-state` | `/repositoryId` | 90 days | `PrStateStore` (`Infra/Cosmos/`) |
| `reviews` | `/repositoryId` | none | `ReviewStore` (`Infra/Cosmos/`) |
| `sessions` | `/conversationId` | 180 days | `CosmosChatHistoryProvider` (MAF built-in) |

GitHub repository IDs use `owner__repo` format (not `owner/repo`) because Cosmos
`ReadItemAsync` treats `/` as a path separator in the document URI. The `/` → `__`
replacement happens at the entry points (webhook `ToRequest`, admin registration).

**repositories** — one document per registered repo. Gates webhooks (unregistered repos ignored). Holds provider, enabled flag, name, URL, and `lastReviewedAt` (denormalized from latest review).

**pr-state** — one document per PR, keyed `{repositoryId}-pr-{pullRequestId}`. Tracks last reviewed iteration for incremental diffs. 90-day TTL since merged PRs don't need tracking.

**reviews** — one document per review event. Captures status, findings count by severity, token usage, duration, and `conversationId` link to the AI session. Foundation for history and metrics.

**sessions** — AI conversation history. Managed by MAF's `CosmosChatHistoryProvider`. 180-day TTL. Linked from review documents for debugging.

# Local testing

Local development uses Azurite for queues. The VS Code task `start azurite` launches it
automatically before the func host. Alternatively, point `AzureWebJobsStorage` in
`local.settings.json` to a real Azure Storage account.

To test with real ADO webhooks locally: create a persistent dev tunnel so the URL survives
restarts (no need to update ADO each time):

```bash
devtunnel create aporia --allow-anonymous
devtunnel port create aporia -p 7071
devtunnel host aporia          # prints the fixed URL
```

On subsequent sessions, just `devtunnel host aporia` — same URL.

ADO service hooks needed for local testing:

| Event | Route | Triggers |
|---|---|---|
| `Pull request created` | `{tunnel-url}/api/webhook/ado` | Review pipeline |
| `Pull request updated` | `{tunnel-url}/api/webhook/ado` | Incremental review |
| `Pull request commented on` | `{tunnel-url}/api/webhook/ado/comment` | Chat pipeline |

Then `func start` — PR events and `@aporia` comments on any registered repo will flow through
the local pipeline.

For GitHub, point the App's webhook URL to the tunnel and subscribe to the needed events:

1. `devtunnel host aporia` — same persistent URL
2. In your GitHub App settings (**General** tab), set Webhook URL to
   `{tunnel-url}/api/webhook/github?code=<function-key>`
3. Verify event subscriptions (**Permissions & events** tab → Subscribe to events):
   Pull request, Pull request review comment, Issue comment.
   These are **not checked by default** — you must enable them manually after setting
   permissions.
4. `func start` — PR events, replies on Aporia threads, and `@aporia` mentions will flow
   through the local pipeline. Restore the production URL when done.
