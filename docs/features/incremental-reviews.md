# Incremental Reviews

## Problem

Every `pullrequest.updated` webhook reviews the entire PR from scratch. A PR with 5 feedback rounds
gets 5 full reviews with duplicate comments accumulating.

Two mechanisms solve this: iteration tracking (skip redundant work, narrow scope) and comment dedup
(don't repost findings that already exist on the PR).

## How It Works

The pipeline in `ReviewFunction` is unchanged:

```csharp
var config = await git.GetConfig(req);
var diff = await git.GetDiff(req, config);

if (diff.Files.Count == 0) return;  // nothing new — skip LLM

var result = await reviewer.Review(req, diff, config);
await git.PostReview(req, diff, result);
```

All incremental logic lives inside the connector. `GetDiff` narrows the file set. `PostReview`
deduplicates and saves state.

### Iteration tracking — `GetDiff`

`AdoConnector.GetDiff` queries `IPrStateStore` for the last reviewed cursor:

- **No state (first review)**: full PR diff. `compareTo` is `null` → diff vs target branch.
- **State exists, same cursor**: returns empty `Diff` (0 files). `ReviewFunction` skips the LLM.
- **State exists, new cursor**: passes `compareTo` to
  `GetPullRequestIterationChangesAsync`. Returns only files changed since last review.

The diff content for each file is still computed against the target branch (full accumulated change).
We narrow the *file set*, not the *diff depth*. The reviewer sees complete context for the files it
reviews.

`Diff` carries a provider-agnostic cursor (ADO iteration ID, GitHub commit SHA, etc.):

```csharp
public record Diff(List<FileChange> Files, string? Cursor = null);
```

### Comment dedup — `PostReview`

Each finding gets a fingerprint: SHA256 of `"{filePath}|{normalizedMessage}"`. Position-independent
(line numbers shift between iterations). Case-insensitive, trimmed.

Before posting, `PostReview` fetches all existing threads and collects fingerprints from Aporia
threads (identified by `aporia:version` property). If a finding's fingerprint already exists, it's
skipped.

New threads are stamped with:
- `aporia:version` = `"1"`: identifies Aporia-created threads
- `aporia:fingerprint` = the SHA256 hash, used for future dedup

### State persistence — `PostReview`

After posting, `PostReview` saves the cursor to `IPrStateStore`:

```csharp
await stateStore.SaveAsync(req.RepositoryId, req.PullRequestId, diff.Cursor);
```

### Empty diff = skip

When `GetDiff` returns 0 files, `ReviewFunction` logs and returns early. No LLM call, no posting.

## Session history

The reviewer and explorer conversations are persisted in Cosmos via `CosmosChatHistoryProvider`,
keyed by `conversationId` = `pr-{repositoryId}-{pullRequestId}`. Messages have a 180-day TTL.

This means successive reviews of the same PR **accumulate**: each new review sees the prior runs'
messages. This is intentional: when a new iteration arrives, the reviewer has context from the
previous review (what it found, what it investigated). It avoids re-investigating the same hypotheses.

The trade-off is token cost: a PR with many iterations will have a growing session. The TTL bounds
this naturally (messages expire after 180 days), and most PRs don't live that long.

## Persistence

`IPrStateStore` persists the last reviewed cursor per PR in Cosmos DB.
Partition key: `/repositoryId`. Document ID: `{repositoryId}-pr-{pullRequestId}`.

Configuration:

```json
{
  "Aporia": { "EnableIncrementalReviews": true },
  "Cosmos": { "ConnectionString": "..." }
}
```

When `EnableIncrementalReviews` is true, each PR update only reviews changes since the last review. When
false, the PR is reviewed once on creation and subsequent updates are skipped. Comment dedup
(fingerprinting) is always active regardless of this flag. The Cosmos connection string is required.
The database and containers (`sessions`, `pr-state`) are created automatically at startup.

## Design decisions

**No auto-closing of stale threads.** An earlier design resolved threads whose fingerprint
disappeared from new results. This was removed because LLM non-determinism means the same conceptual
issue gets different wording across runs → different fingerprint → spurious "resolved" comments.
Developers resolve threads manually.

**Fingerprint is message-based, not position-based.** Line numbers shift between iterations. Using
file + message makes dedup stable across code movement.

**State lives in the connector, not the function.** Iteration checking, dedup, and state saving are
all inside `AdoConnector`. `ReviewFunction` stays simple and doesn't know about iterations.
