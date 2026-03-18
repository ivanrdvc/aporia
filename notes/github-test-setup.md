# GitHub Integration Test Setup

Steps taken to set up GitHub provider integration testing, and inputs for updating
the `/test` skill to be provider-aware.

## What Was Done

### 1. GitHub test repo

- Forked `dotnet/eShop` → `ivanrdvc/eShop`
- Created PR #1 ("Order tracking notifications") with identical changes to ADO PR 12
  (15 files, 489 insertions — same planted bugs)
- Method: fetched ADO branch via PAT, generated patch, applied to GitHub fork

### 2. Test infrastructure refactored to be provider-agnostic

- **`ITestHelper`** interface (`BuildRequest`, `CleanComments`, `GetRevuCommentCount`, `PrintComments`)
- **`AdoTestHelper`** — ADO implementation (replaced static `AdoThreadHelper`)
- **`GitHubTestHelper`** — GitHub implementation (uses REST API via `HttpClient`)
- **`IntegrationTestBase`** — `GitClient` (ADO-specific) replaced with `ITestHelper`
- **`AppFixture`** — provider-aware switch on `TestRepoOptions.Provider`
- **`Scenarios.cs`** — added `GitHubMultiAgentCrossService` (PR #1 on `ivanrdvc/eShop`)

### 3. Expectations

- `expectations.json` updated with GitHub PR 1 entry under key `gh:ivanrdvc/eShop:1`
- Same 4 required + 4 optional findings as ADO PR 12

### 4. Secrets configured

Both `Revu.csproj` and `Revu.Tests.Integration.csproj` user-secrets:

```
GitHub:Token = <gh-pat>
```

For GitHub App auth (used in production, optional for tests):

```
GitHub:AppId = <app-id>
GitHub:PrivateKey = <pem-contents>
```

### 5. Config switching

`appsettings.test.json` — flip `TestRepo.Provider` between `Ado` and `GitHub`:

**GitHub:**
```json
"TestRepo": {
  "Provider": "GitHub",
  "Organization": "ivanrdvc",
  "Project": "",
  "RepositoryId": "ivanrdvc/eShop",
  "RepositoryName": "eShop"
},
"TestTarget": {
  "PrId": 1,
  "Branch": "refs/heads/feature/order-tracking-notifications"
}
```

**ADO (original):**
```json
"TestRepo": {
  "Provider": "Ado",
  "Organization": "ivanradovic",
  "Project": "ivanrndvc-sc",
  "RepositoryId": "068b4389-3bae-438c-a0a7-08619db2b998",
  "RepositoryName": "ivanrndvc-sc"
},
"TestTarget": {
  "PrId": 12,
  "Branch": "refs/heads/feature/order-tracking-notifications"
}
```

## Inputs for `/test` Skill Update

The `/test` skill needs provider-aware paths. Here's what changes per command:

### `/test` (run pipeline test + verify)

No changes to the test command itself — `Review_FullPipeline_PostsFindings` is provider-agnostic.
`verify.py` and session analysis are also provider-agnostic.

The only prerequisite: `appsettings.test.json` must point at the right provider before running.

### `/test run` (end-to-end webhook flow)

Provider-specific sections needed:

| Step | ADO | GitHub |
|------|-----|--------|
| Create PR | `az repos pr create ...` | `gh pr create ...` |
| Webhook endpoint | `POST /webhook/ado` | `POST /webhook/github` |
| Webhook payload | `AdoWebhook` JSON shape | `GitHubWebhook` JSON shape |
| Result inspection | `az devops invoke` (threads) | `gh api repos/{owner}/{repo}/pulls/{pr}/reviews` |

The skill should read `TestRepo.Provider` from `appsettings.test.json` and branch accordingly.

**GitHub webhook payload shape:**
```json
{
  "action": "opened",
  "number": 1,
  "pull_request": {
    "number": 1,
    "draft": false,
    "head": { "ref": "feature/order-tracking-notifications", "sha": "..." },
    "base": { "ref": "main" }
  },
  "repository": {
    "id": 123,
    "name": "eShop",
    "full_name": "ivanrdvc/eShop",
    "owner": { "login": "ivanrdvc" }
  }
}
```

### `/test cleanup`

- ADO: runs `DeleteAllComments` test (current behavior)
- GitHub: `CleanComments` is a no-op (edit-in-place handles it). Could add dismiss-all-reviews
  via `gh api`, but low priority.

### `/test session`

No changes — session JSON and `verify.py` are provider-agnostic.

### `expectations.json`

GitHub PR 1 is keyed as `gh:ivanrdvc/eShop:1`. The `verify.py` matching logic may need to
handle this key format (currently expects numeric PR IDs). Check how `verify.py` resolves the
key — it likely reads the PR ID from the test log/config and looks it up.

## Files Changed

```
tests/Revu.Tests.Integration/
  Fixtures/
    ITestHelper.cs                  (new)
    AdoTestHelper.cs                (new — replaces AdoThreadHelper.cs)
    GitHubTestHelper.cs             (new)
    AdoThreadHelper.cs              (deleted)
    AppFixture.cs                   (provider-aware switch)
    IntegrationTestBase.cs          (GitClient → ITestHelper)
    Scenarios.cs                    (added GitHub scenario, ADO uses private helper)
  ReviewTests.cs                    (TestHelper instead of GitClient)
  IncrementalReviewTests.cs         (TestHelper instead of GitClient)
  CleanupTests.cs                   (TestHelper instead of GitClient)
  FixtureCaptureTests.cs            (TestHelper.BuildRequest instead of AdoThreadHelper)
  appsettings.test.json             (can now switch Provider to GitHub)
.claude/skills/test/scripts/expectations.json  (added gh:ivanrdvc/eShop:1)
docs/setup.md                      (added GitHub setup instructions)
notes/plans/2026-03-14-github-support.md  (updated test repo setup, added pre-impl section)
```
