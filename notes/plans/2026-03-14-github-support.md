---
date: 2026-03-14
status: done
tags: [github, git-connector, webhook, multi-provider]
---

# Add GitHub Support

## Problem Statement

Aporia currently only supports Azure DevOps as a git provider. The `IGitConnector` interface and
`GitProvider` enum already declare GitHub as a valid provider, but no implementation exists.
Adding GitHub support would let Aporia review PRs on GitHub repositories using the same pipeline
(WebhookFunction → queue → ReviewFunction) that ADO uses today.

## Decision Drivers

- **Minimal disruption to the review pipeline.** ReviewFunction, Reviewer, CoreStrategy, and all
  models (Diff, FileChange, ReviewResult) are already provider-agnostic — they must stay that way.
- **Parity with ADO features.** GitHub connector must support: config fetch, diff, post review,
  file read, list files, code search, PR context, incremental reviews, and comment deduplication.
- **Each connector owns its clients.** No provider-specific types leak into DI. Each connector
  takes its own typed options and builds/caches clients internally.
- **Webhook security.** GitHub webhooks should be validated via HMAC signature (`X-Hub-Signature-256`).
- **Keep providers similar where possible.** Same interface, same cursor abstraction, same
  org-keyed convention — but no shared base class or forced symmetry where platforms differ.

## Solution

Implement `GitHubConnector : IGitConnector` using raw `HttpClient` with GitHub REST API. Add a
GitHub webhook endpoint and webhook model. Switch DI from a single `IGitConnector` registration
to keyed services so ReviewFunction resolves the correct connector per `ReviewRequest.Provider`.

### DI Strategy — Keyed Services

Already used for `IReviewStrategy`. Same pattern:

```csharp
services.AddKeyedSingleton<IGitConnector, AdoConnector>(GitProvider.Ado);
services.AddKeyedSingleton<IGitConnector, GitHubConnector>(GitProvider.GitHub);
```

ReviewFunction resolves via `ReviewRequest.Provider`:

```csharp
public class ReviewFunction(IServiceProvider sp, Reviewer reviewer, ...)
{
    public async Task Run([QueueTrigger("review-queue")] ReviewRequest req)
    {
        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);
        // rest unchanged
    }
}
```

### Multi-Org — Connectors Own Their Clients

Each connector takes its own `IOptions<T>` and manages clients internally:

- **AdoConnector** takes `IOptions<AdoOptions>`, builds `ConcurrentDictionary<string, GitHttpClient>`
  lazily. The two `IReadOnlyDictionary` DI registrations in `AddAdoClient()` are removed.
- **GitHubConnector** takes `IOptions<GitHubOptions>`, builds org-keyed `HttpClient` instances
  internally. PAT auth to start, GitHub App auth later.
- `AddAdoClient()` / `AddGitHubClient()` simplify to just options registration + validation.

### Webhooks — Two Methods, One Class, Same Queue

```
POST /webhook/ado    → WebhookAdo()    → review-queue
POST /webhook/github → WebhookGitHub() → review-queue
```

ReviewFunction stays single — resolves the right connector by provider key.

### GitHub Review Posting — Single Review API Call

GitHub has a first-class review concept. One `POST /repos/{owner}/{repo}/pulls/{pr}/reviews`:

**Posting style: Option B (split).** Summary as editable issue comment (`<!-- aporia:summary -->`
marker), findings as separate review. Two API calls. This is the only option that supports clean
re-reviews (PATCH summary, dismiss+repost findings). Step 0 spike validates the visual layout
before implementation — the architectural decision is made.

Review payload:

```json
{
  "commit_id": "head_sha_here",
  "event": "COMMENT",
  "body": "...",
  "comments": [
    {
      "path": "src/Foo.cs",
      "line": 42,
      "start_line": 40,
      "side": "RIGHT",
      "start_side": "RIGHT",
      "body": "Finding message\n\n```suggestion\nfixed code\n```"
    }
  ]
}
```

- **`commit_id` is required** — pins review to specific commit SHA. Without it GitHub defaults
  to HEAD which can race if a push happens between diff fetch and review post. Use the same
  `head.sha` stored as cursor.
- **`start_side: "RIGHT"`** required alongside `side: "RIGHT"` for multi-line comments.
- `line` must fall within a diff hunk. **Out-of-hunk fallback**: `subject_type: "file"` is NOT
  supported in the batch `comments[]` array — only via individual `POST /pulls/{pr}/comments`.
  Rather than posting orphaned file-level comments (which won't be grouped in the review), fold
  out-of-hunk findings into the review `body` as a bullet list. This keeps everything in one
  review event. The `Reviewer` already does something similar — excess findings beyond
  `MaxComments` are folded into the summary via `BuildSummary`.
- **422 fallback:** GitHub doesn't report which comment caused the failure. On 422:
  1. Retry every comment individually (single-comment review per attempt).
  2. On individual 422: strip ` ```suggestion ` block (most common cause — suggestion range
     doesn't match diff) and retry as plain comment.
  3. If still 422: convert multi-line to single-line (`drop start_line`/`start_side`) and retry.
  4. If still 422: skip the comment, log warning with path + line for debugging.
- `CodeFix` renders as ` ```suggestion ` block (GitHub natively supports "Apply suggestion" button).
  **Deleted-line limitation:** suggestion blocks don't work when the line range includes deleted
  lines (paired delete+add in the diff). When a finding's range spans deletions, drop `CodeFix`
  (post finding as plain comment without the suggestion). Detect by checking the diff hunk for
  `-` lines within the `start_line..line` range.
- Fingerprint for dedup: embed as HTML comment `<!-- aporia:fp:abc123 -->` in comment body.

**Dedup strategy:**
- Fetch all existing review comments in one call: `GET /pulls/{pr}/comments` (paginated, returns
  all review comments across all reviews). Parse `<!-- aporia:fp:... -->` from bodies.
- Single paginated call, no N+1 problem.

**Re-review behavior — edit in place:**
- On re-review, `PostReview` finds the previous aporia review by scanning for the fingerprint
  marker (`<!-- aporia:review -->`) in existing reviews/comments.
- **Summary**: posted as an issue comment (not in the review body) so it can be edited in place
  across runs. On re-review, find the previous summary comment and `PATCH` it. One summary
  comment per PR, always current.
- **Inline findings**: dismiss the previous aporia review (`PUT /reviews/{id}/dismissals`), then
  post a new review with current findings. Only one active set of inline comments at a time.
- This keeps the PR timeline clean regardless of how many re-reviews happen — critical for
  both production use and high-volume testing.

**Outdated comments:** When GitHub receives a push after a review, existing review comments get
marked "outdated" automatically. No need to dismiss or delete old reviews on push — GitHub
handles this natively. Dismiss is only needed when we actively re-review the same commit range.
**Policy: never delete old review comments.** Deleting risks losing reply threads where developers
discussed the finding (Reviewdog learned this — they skip deletion if a comment has replies, but
that's extra complexity). Leave outdated comments as-is; GitHub's "outdated" badge is sufficient.

### GitHub Diffs — Patch From API, Content Still Fetched

GitHub's `GET /pulls/{pr}/files` returns `patch` field directly (unified diff) — no need to run
`DiffBuilder` like ADO does. But `FileChange.Content` (full file source, used by reviewer tools)
still requires per-file content fetches, same as ADO. Pagination required: max 300 files per page,
3,000 files total per PR. Follow `Link` header for pagination. Bail early with a warning log if
a PR exceeds 3,000 files (analogous to ADO's 5,000-file cap).

### Incremental Reviews — Cursor as Commit SHA

- ADO cursor = iteration ID. GitHub cursor = `head.sha` at time of review.
- `PrStateStore` is provider-agnostic (cursor is `string?`), no changes needed.
- On re-review: compare current head SHA against stored cursor SHA using
  `GET /repos/{owner}/{repo}/compare/{cursor}...{head}` to get files changed since last review.
- Force-push edge case: stored cursor SHA won't exist in new history. Detect (404) and fall
  back to full diff.
- **Compare API 250-file cap:** `GET /compare/{base}...{head}` returns at most 250 files and
  300 commits. If exceeded, result is truncated with no indication. Fallback: full diff (same
  as force-push case).

## Research Summary

### Current Architecture

**IGitConnector** (`Aporia/Git/IGitConnector.cs`): 7 methods — `GetConfig`, `GetDiff`, `PostReview`,
`GetFile`, `ListFiles`, `SearchCode`, `GetPrContext`. All take `ReviewRequest` as first arg.

**AdoConnector** (`Aporia/Git/AdoConnector.cs`): Currently takes two org-keyed dictionaries via DI.
Key behaviors: incremental reviews via iteration comparison, comment dedup via SHA256 fingerprint,
parallel file reads (10 concurrent), DiffBuilder for unified diff hunks.

**WebhookFunction** (`Aporia/Functions/WebhookFunction.cs`): Single route `POST /webhook/ado`.
Reads `AdoWebhook`, calls `ToRequest()`, validates repo in RepoStore, injects Organization, enqueues.

**ReviewFunction** (`Aporia/Functions/ReviewFunction.cs`): Injects `IGitConnector` directly (not keyed).
Pipeline: GetConfig → GetDiff → Review → PostReview.

**RepoStore**: `Repository` already has `GitProvider Provider` field.

**Models.cs**: `ReviewRequest` has `GitProvider Provider` and `Organization` fields.

**PrStateStore**: Cursor is a plain string — provider-agnostic.

### GitHub API Mapping

| IGitConnector method | GitHub REST API |
|---------------------|-----------------|
| `GetConfig` | `GET /repos/{owner}/{repo}/contents/.aporia.json?ref={targetBranch}` |
| `GetDiff` | `GET /repos/{owner}/{repo}/pulls/{pr}/files` (paginated, max 300/page) |
| `PostReview` | `POST /repos/{owner}/{repo}/pulls/{pr}/reviews` (batch) |
| `GetFile` | `GET /repos/{owner}/{repo}/contents/{path}?ref={sourceBranch}` |
| `ListFiles` | `GET /repos/{owner}/{repo}/contents/{path}?ref={sourceBranch}` or Trees API |
| `SearchCode` | `GET /search/code?q={query}+repo:{owner}/{repo}` |
| `GetPrContext` | `GET /repos/{owner}/{repo}/pulls/{pr}` + commits endpoint |

### Competitor Posting Patterns

Three approaches observed in production AI reviewers:

**Option A — all-in-one review (used by GitHub's own Copilot reviewer):**
- Summary + findings in one review: `body` = summary, `comments[]` = all findings.
- All comments share a single `pull_request_review_id` — one grouped review event.
- Uses `state: COMMENTED`, `subject_type: line`.
- No edit-in-place — each re-review creates a new review event in the timeline.

**Option B — split: issue comment + review (used by two major open-source AI reviewers):**
- **Summary**: issue comment (`POST /issues/{n}/comments`), editable in place via HTML comment
  marker (finds by tag, PATCHes on re-review).
- **Inline findings**: separate review (`POST /pulls/{n}/reviews`) with comments array.
- Two separate API calls — summary and findings are not in the same request.
- On re-review: PATCH summary comment, dismiss/delete old review, post new review.
- Fallback pattern on 422: verify each comment individually, retry valid ones, skip invalid.

**Option C — pending then submit (variant of B, used by one open-source reviewer):**
- Same split as B, but creates review as pending first (omit `event`), then submits via
  `POST /pulls/{n}/reviews/{id}/events` with `event: COMMENT`.
- Avoids partial reviews if process crashes mid-post. More complex, marginal safety benefit.

### GitHub API: Three Comment Types

| Type | API | Editable | Attached to code |
|------|-----|----------|-----------------|
| Issue comment | `POST /issues/{n}/comments` | Yes (PATCH) | No |
| Review | `POST /pulls/{n}/reviews` | No (dismiss only) | Body no, comments yes |
| Review comment | Part of review `comments[]` | No | Yes (diff lines) |

## Implementation Steps

### Step 0 — API spike: validate Option B visual layout

**Goal:** Post mock review results to a test PR using `gh api` to validate how Option B
(split: issue comment + review) looks in the GitHub UI before writing code.

**Decision is made** — Option B is the only approach that supports clean re-reviews. This spike
confirms the visual layout is acceptable.

```bash
HEAD_SHA=$(gh api repos/{owner}/{repo}/pulls/{pr} --jq '.head.sha')

# 1. Summary as issue comment (editable via PATCH on re-review)
gh api repos/{owner}/{repo}/issues/{pr}/comments \
  -f body='<!-- aporia:summary -->
### Pull request overview
Summary text here. Aporia reviewed 3 files.

<details><summary>Show a summary per file</summary>

| File | Description |
|---|---|
| src/Foo.cs | Added validation logic |
</details>'

# 2. Findings as review (dismiss + repost on re-review)
gh api repos/{owner}/{repo}/pulls/{pr}/reviews \
  -f commit_id="$HEAD_SHA" \
  -f event=COMMENT \
  -f body='<!-- aporia:review -->' \
  -f 'comments=[
    {"path":"src/Foo.cs","line":42,"side":"RIGHT","body":"<!-- aporia:fp:a1b2c3 -->\nMissing null check on `input` — will throw if caller passes null.\n\n```suggestion\nif (input is null) throw new ArgumentNullException(nameof(input));\n```"},
    {"path":"src/Bar.cs","line":15,"start_line":12,"side":"RIGHT","start_side":"RIGHT","body":"<!-- aporia:fp:d4e5f6 -->\nHttpClient created per request — can cause socket exhaustion under load."}
  ]'
```

**To run:**
1. Create a throwaway PR on a test GitHub repo.
2. `gh auth login` if not already authenticated.
3. Run the commands above, inspect the PR UI.
4. Verify: summary appears as a standalone comment, findings appear grouped under one review.

### Step 1 — Refactor ADO client ownership

**Files:** `Aporia/Git/AdoConnector.cs`, `Aporia/Infra/ServiceCollectionExtensions.cs`

- AdoConnector: replace two `IReadOnlyDictionary` constructor params with `IOptions<AdoOptions>`.
  Add private `ConcurrentDictionary` fields, build clients lazily via `GetOrAdd`.
- `AddAdoClient()`: remove the two `AddSingleton<IReadOnlyDictionary<...>>` blocks. Keep just
  options registration and validation.
- **Regression gate:** this is the only step that refactors working production code. Run ADO
  integration tests before and after to verify no regression.

### Step 2 — Switch to keyed services and thread connector through the call chain

**Files:** `Aporia/Program.cs`, `Aporia/Functions/ReviewFunction.cs`, `Aporia/Review/IReviewStrategy.cs`,
`Aporia/Review/Reviewer.cs`, `Aporia/Review/CoreStrategy.cs`

**The problem:** Currently `CoreStrategy` receives `IGitConnector` via DI constructor injection
(line 19 of `CoreStrategy.cs`). When we switch to keyed registrations, the DI container has no way
to know which key to use — `ReviewFunction` resolves the right connector manually via
`sp.GetRequiredKeyedService<IGitConnector>(req.Provider)`, but that instance never reaches the
strategy. The strategy factory (`strategyFactory(config.Review.Strategy)`) triggers DI resolution
of `CoreStrategy`, which would need a non-keyed `IGitConnector` that no longer exists.

**The fix — pass `IGitConnector` through the call chain (Option A):**

1. **`IReviewStrategy.Review`** — add `IGitConnector git` parameter:
   ```csharp
   Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config,
       IGitConnector git, CodeGraphQuery? codeGraph = null, CancellationToken ct = default);
   ```

2. **`Reviewer.Review`** — add `IGitConnector git` parameter, pass through:
   ```csharp
   public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config,
       IGitConnector git, CancellationToken ct = default)
   {
       // ...
       var result = await strategy.Review(req, diff, config, git, codeGraph, ct);
   }
   ```

3. **`CoreStrategy`** — remove `IGitConnector git` from constructor, receive it in `Review`:
   ```csharp
   public class CoreStrategy(
       [FromKeyedServices(ModelKey.Reasoning)] IChatClient reviewerClient,
       [FromKeyedServices(ModelKey.Default)] IChatClient explorerClient,
       // IGitConnector removed from here
       ChatHistoryProvider sessionProvider,
       FileAgentSkillsProvider skillsProvider,
       PrContextProvider prContextProvider,
       ILogger<CoreStrategy> logger) : IReviewStrategy
   {
       public async Task<ReviewResult> Review(ReviewRequest req, Diff diff, ProjectConfig config,
           IGitConnector git, CodeGraphQuery? codeGraph = null, CancellationToken ct = default)
       {
           var tools = new ReviewerTools(git, req, diff, codeGraph);  // unchanged
           // ...
       }
   }
   ```

4. **`ReviewFunction`** — resolves connector, passes to `reviewer.Review`:
   ```csharp
   public class ReviewFunction(IServiceProvider sp, Reviewer reviewer, ...)
   {
       public async Task Run([QueueTrigger("review-queue")] ReviewRequest req)
       {
           var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);
           var config = await git.GetConfig(req);
           var diff = await git.GetDiff(req, config);
           var findings = await reviewer.Review(req, diff, config, git);
           await git.PostReview(req, diff, findings);
       }
   }
   ```

5. **Program.cs** — keyed registrations, remove non-keyed singleton:
   ```csharp
   builder.Services.AddKeyedSingleton<IGitConnector, AdoConnector>(GitProvider.Ado);
   // GitHubConnector added in Step 7
   ```

`ReviewerTools` is unchanged — it already receives `git` as a constructor parameter from
`CoreStrategy`, not from DI. The change just moves where `CoreStrategy` gets that instance:
from DI injection to method parameter.

### Step 3 — GitHub options and client registration

**Files:** `Aporia/Git/GitHubOptions.cs` (new), `Aporia/Infra/ServiceCollectionExtensions.cs`

- `GitHubOptions` with `Dictionary<string, GitHubOrgConfig>` (each has `Owner` and `Token`).
  `WebhookSecret`: single global secret for HMAC validation.
- `AddGitHubClient()`: just options registration + validation.
- GitHubConnector owns its `HttpClient` instances internally (built from options, cached per org).
- **Org lookup:** webhook payload supplies `repository.owner.login`. `RepoStore.Repository.Organization`
  holds the config key (set at registration time via AdminFunction). GitHubConnector uses
  `req.Organization` to look up `GitHubOptions.Organizations[key]` for the PAT — same indirection
  as ADO. The config key doesn't have to match the literal owner name.

### Step 4 — GitHub webhook model

**Files:** `Aporia/Git/GitHubWebhook.cs` (new)

- Records for GitHub webhook payload: action, pull_request, repository.
- `ToRequest()`: filter to `opened`/`synchronize`/`reopened`, skip drafts, map to `ReviewRequest`.

### Step 5 — GitHub webhook endpoint

**Files:** `Aporia/Functions/WebhookFunction.cs`

- Add `WebhookGitHub()` method, route `POST /webhook/github`.
- Validate `X-Hub-Signature-256` HMAC.
- Check `X-GitHub-Event` header is `pull_request`.
- Same `WebhookResponse` output binding to `review-queue`.

### Step 6 — Implement GitHubConnector

**Files:** `Aporia/Git/GitHubConnector.cs` (new)

- Constructor: `IOptions<GitHubOptions>`, `IPrStateStore`, `IOptions<AporiaOptions>`,
  `ILogger<GitHubConnector>`.
- `GetConfig`: Contents API at target branch, base64-decode, `ProjectConfig.Parse()`.
- `GetDiff`: PR files API with `per_page=300` (paginate via `Link` header, bail at 3,000 files),
  patches from API directly (no DiffBuilder), content via per-file fetches (parallel, 10
  concurrent), cursor = head SHA, incremental via compare API (fall back to full diff if
  compare returns 250+ files — truncation cap).
- `PostReview` (Option B — split: issue comment for summary, review for findings):
  - Always include `commit_id` (head SHA) to pin review to specific commit.
  - Always include `start_side: "RIGHT"` alongside `side: "RIGHT"` for multi-line comments.
  - Hunk validation: check each finding's lines fall within a diff hunk.
  - Out-of-hunk findings: fold into review body as bullet list (not separate API calls).
  - CodeFix as ` ```suggestion ` block. Drop CodeFix when range spans deleted lines.
  - 422 fallback: retry all individually → strip suggestion → single-line → skip (see above).
  - Dedup: `GET /pulls/{pr}/comments` (single paginated call for all review comments across
    all reviews), parse `<!-- aporia:fp:... -->` from bodies.
  - Fingerprint embed as `<!-- aporia:fp:abc123 -->` in comment body.
  - Re-review: find previous summary comment by `<!-- aporia:summary -->` marker, PATCH it.
    Find previous review by `<!-- aporia:review -->` marker in review body, dismiss it.
    Post new review with current findings.
  - **30-comment limit:** GitHub caps `comments[]` at 30 per review API call. `MaxComments`
    defaults to 5 so this is unlikely to hit, but if exceeded, batch into multiple reviews.
- `GetFile`: Contents API at source branch, base64-decode. Files >1MB via Blob API.
- `ListFiles`: Contents API or Trees API.
- `SearchCode`: GitHub code search API (note: 10 req/min rate limit). On rate limit (403),
  return an error message the LLM can see: "Search unavailable (rate limited)" — not empty
  results, so the reviewer knows to proceed without search rather than thinking there are no
  matches.
- `GetPrContext`: PR details + commits.

**Rate limit handling:** GitHub has secondary rate limits for content-creating endpoints.
Add retry-after header handling for 403 responses.

### Step 7 — Wire up in Program.cs

**Files:** `Aporia/Program.cs`

- Call `AddGitHubClient()` alongside `AddAdoClient()`.
- `AddKeyedSingleton<IGitConnector, GitHubConnector>(GitProvider.GitHub)`.

### Step 8 — Unit tests

**Files:** `tests/Aporia.Tests.Unit/Git/GitHubConnectorTests.cs` (new),
`tests/Aporia.Tests.Unit/Git/GitHubWebhookTests.cs` (new)

- Webhook parsing: action filtering, draft filtering, field mapping.
- Connector: fingerprint dedup, cursor handling, hunk validation for review posting.
- Mock `HttpClient` responses with realistic GitHub API payloads.

Integration tests are covered in the Post-Implementation section below.

## What Does NOT Change

- `IGitConnector` interface
- `ReviewRequest`, `Diff`, `FileChange`, `Finding`, `ReviewResult` models
- `PrStateStore` (cursor is already a string)
- `Prompts` — completely unaware of provider
- `RepoStore` / `Repository` — already has `GitProvider` field
- `ReviewerTools` — already receives `IGitConnector` as constructor parameter

## What Changes Slightly

- `IReviewStrategy.Review` — gains `IGitConnector git` parameter (threaded from caller)
- `Reviewer.Review` — gains `IGitConnector git` parameter (passes through to strategy)
- `CoreStrategy` — removes `IGitConnector` from DI constructor, receives via `Review` parameter
- `ReviewFunction` — takes `IServiceProvider` instead of `IGitConnector`, resolves by provider key

These are signature changes only — no logic changes. All three remain provider-agnostic (they
receive an `IGitConnector`, they don't know or care which implementation it is).

## Decided Questions

- [x] **Posting style:** Option B (split — issue comment for summary, review for findings).
- [x] **Webhook secret:** One global secret. Per-org adds complexity for marginal benefit —
  repo registration already gates which repos trigger reviews.
- [x] **SearchCode rate limit:** Return error message visible to the LLM, not empty results.
- [x] **Auth:** PAT to start, GitHub App auth later.

## Open Questions

- [ ] **Rework /test skill.** Post-implementation — redesign the skill to be provider-agnostic
  and improve the workflow for running tests. Details TBD.

## Pre-Implementation: Integration Test Infrastructure Refactor

This can be done NOW, before the connector exists. The goal is to make the test infrastructure
provider-agnostic so that when `GitHubConnector` lands, it just plugs in.

### Step A — ITestHelper abstraction + refactor IntegrationTestBase

**Independent of connector implementation.** Extract provider-specific test helpers behind an
interface. Remove ADO-specific `GitClient` from `IntegrationTestBase`.

### Step B — Provider-aware AppFixture

**Partially independent.** Make AppFixture conditionally register services based on
`TestRepoOptions.Provider`. GitHub connector registration can be a stub/TODO until Step 6 lands.

### Step C — Add GitHub scenarios and expectations

**Independent.** Add GitHub PR 1 to `Scenarios.cs` and `expectations.json`. Same planted bugs
as ADO PR 12.

## Post-Implementation: Integration Tests & /test Skill

This is crucial — the GitHub connector isn't done until we can run integration tests against it
and use `/test` to iterate on review quality, just like we do with ADO today.

### Integration test infrastructure

**AppFixture** (`tests/Aporia.Tests.Integration/Fixtures/AppFixture.cs`) must become provider-aware:
- Read `TestRepoOptions.Provider` and register the correct keyed connector + client setup.
- When provider is `GitHub`: register `GitHubConnector`, skip `AddAdoClient()`.
- When provider is `Ado`: register `AdoConnector`, skip `AddGitHubClient()` (current behavior).

**Test helper abstraction** — replace `AdoThreadHelper` with `ITestHelper`:

```csharp
interface ITestHelper
{
    ReviewRequest BuildRequest(int prId, string branch);
    Task PrepareForRun(ReviewRequest req);           // ADO: clean threads. GitHub: no-op (edit-in-place handles it)
    Task<List<PostedComment>> GetAporiaComments(ReviewRequest req);  // ADO: threads. GitHub: latest review comments
    Task PrintComments(ReviewRequest req, ITestOutputHelper output);
}
```

- `AdoTestHelper`: wraps current `AdoThreadHelper` logic (uses `GitHttpClient`).
- `GitHubTestHelper`: uses GitHub REST API (`HttpClient`) to fetch review comments, print them.
  `PrepareForRun` is a no-op — the connector's edit-in-place behavior keeps the PR clean.
- `IntegrationTestBase`: takes `ITestHelper` from DI instead of using `AdoThreadHelper` directly.
  `GitClient` property (ADO SDK type) is removed from base class.
- `AppFixture` registers the correct `ITestHelper` based on provider.

**Scenarios** (`Fixtures/Scenarios.cs`): add GitHub equivalents pointing to a GitHub test repo.
The test repo needs PRs with known bugs (same pattern as the ADO eShop test repo).

**Configuration** — switching providers:

```json
// appsettings.test.json — flip provider to target GitHub
"TestRepo": {
  "Provider": "GitHub",
  "Organization": "my-gh-org",
  "RepositoryId": "owner/repo",
  "RepositoryName": "repo"
}
```

GitHub PAT goes in user-secrets under `GitHub:Organizations:<key>:Token`.

**ReviewTests** — no changes needed. `Git.GetConfig`, `Git.GetDiff`, `Reviewer.Review`,
`Git.PostReview` all go through `IGitConnector`. Tests work for either provider by switching
config.

### /test skill updates

The `/test` skill (`/.claude/skills/test/SKILL.md`) needs provider-aware paths:

**`/test`** — mostly unchanged. Runs `Review_FullPipeline_PostsFindings`, pipes to file, runs
`verify.py`. The test itself is provider-agnostic. `verify.py` analyzes sessions (also
provider-agnostic). No changes needed here beyond ensuring AppFixture works with GitHub.

**`/test run`** — provider-specific sections:
- **PR creation**: `az repos pr create` for ADO, `gh pr create` for GitHub.
- **Webhook payload**: ADO webhook JSON vs GitHub webhook JSON (different shape, different
  endpoint `/webhook/github`).
- **Result inspection**: `az devops invoke` for ADO threads vs `gh api` for GitHub review
  comments.
- The skill should read `TestRepo.Provider` from `appsettings.test.json` and branch accordingly.

**`/test cleanup`** — ADO: runs `DeleteAllComments` test (current behavior). GitHub: no-op or
dismiss all aporia reviews via API. Less important for GitHub since edit-in-place keeps things clean.

**`verify.py`** — no changes. It analyzes session JSON files which are provider-agnostic.

**`expectations.json`** — add GitHub scenario entries keyed by PR ID, same format as existing
ADO entries.

### GitHub test repo setup — DONE

**Repo:** `ivanrdvc/eShop` (fork of `dotnet/eShop`)
**Test PR:** https://github.com/ivanrdvc/eShop/pull/1 — "Order tracking notifications"

Same 15 files / 489 insertions as ADO PR 12 (`ivanrndvc-sc` PR 12). Identical planted bugs —
same expectations apply. Created by applying the ADO PR diff to the GitHub fork.

**Still needed:**
- Register the repo via `POST /manage/repos` with `provider: "github"`.
- Configure webhook subscription pointing to the dev tunnel / deployed endpoint.
- Add `.aporia.json` config file to the repo (or test with defaults).
