# Self-Review Testing on ADO

Run Revu against its own PR on Azure DevOps to verify the full pipeline end-to-end.

## Prerequisites

- Local func tools: `func --version`
- ADO remote configured: `git remote -v` (look for `ado`)
- `local.settings.json` in `Revu/` with valid credentials and queue connection string
- Remote Function App stopped: `az functionapp stop -n func-revu -g rg-revu-prod`
- Repo `revu-ado` registered (repo ID: `4421a1a1-a185-4273-b7a9-58585a559fb1`)

## Steps

### 1. Push main and branch to ADO

ADO's main must be up to date or the PR diff will include unrelated commits.

```bash
git push ado main
git push ado <branch-name>
```

### 2. Create or update a PR

Create:
```bash
curl -s -u ":<PAT>" \
  -X POST "https://dev.azure.com/ivanradovic/ivanrndvc-sc/_apis/git/repositories/revu-ado/pullrequests?api-version=7.1" \
  -H "Content-Type: application/json" \
  -d '{"sourceRefName":"refs/heads/<branch>","targetRefName":"refs/heads/main","title":"<title>"}'
```

If a PR already exists and you need a fresh review, push new commits to create a new iteration.

### 3. Start local func

```bash
cd Revu && func start
```

### 4. Trigger the webhook

```bash
curl -s -X POST "http://localhost:7071/api/webhook/ado" \
  -H "Content-Type: application/json" \
  -d '{
    "eventType": "git.pullrequest.created",
    "resource": {
      "pullRequestId": <PR_ID>,
      "sourceRefName": "refs/heads/<branch>",
      "targetRefName": "refs/heads/main",
      "isDraft": false,
      "repository": {
        "id": "4421a1a1-a185-4273-b7a9-58585a559fb1",
        "name": "revu-ado",
        "project": { "name": "ivanrndvc-sc" }
      }
    }
  }'
```

### 5. Wait and check results

The review takes 30-120s depending on diff size. Check threads:

```bash
curl -s -u ":<PAT>" \
  "https://dev.azure.com/ivanradovic/ivanrndvc-sc/_apis/git/repositories/revu-ado/pullrequests/<PR_ID>/threads?api-version=7.1" \
  | jq '[.value[-10:] | .[] | select(.comments[0].content) | {file: .threadContext.filePath, line: .threadContext.rightFileStart.line, comment: (.comments[0].content[:120])}]'
```

### 6. Clean up

- Kill func: `lsof -ti:7071 | xargs kill -9`
- Restart remote: `az functionapp start -n func-revu -g rg-revu-prod`
- Delete PR comments if needed (use `DELETE` on each thread's comment endpoint)

## Testing the Code Graph

### 1. Start local func

```bash
cd Revu && func start
```

### 2. Re-register the repo to trigger indexing

```bash
curl -s -X POST "http://localhost:7071/api/manage/repos" \
  -H "Content-Type: application/json" \
  -d '{
    "repositoryId": "4421a1a1-a185-4273-b7a9-58585a559fb1",
    "provider": "ado",
    "name": "revu-ado",
    "organization": "ivanradovic",
    "project": "ivanrndvc-sc",
    "defaultBranch": "refs/heads/main"
  }'
```

This upserts the repo and enqueues an `IndexRequest` to `index-queue`. Watch the func console for `Indexing ...` and `Indexing complete ...` logs.

### 3. Verify the index in Cosmos

Open Cosmos Data Explorer → `revu` database → `code-graph` container. Filter by `repoId = "4421a1a1-a185-4273-b7a9-58585a559fb1"`. You should see one document per .cs file with `symbols` and `refs` arrays.

### 4. Run a review with the code graph

Follow the normal self-review steps above (push branch, create PR, trigger webhook). The reviewer now has `QueryCodeGraph` in its tool list. Check if it uses it in the func console logs (tool call traces) or in the OpenTelemetry traces.

### 5. What to look for

- **Indexing**: all .cs files parsed, no errors in func console
- **Query results**: reviewer calls `QueryCodeGraph` for callers/implementations/outline before fetching files
- **Accuracy**: outline of a known file (e.g. `Review/CoreStrategy.cs`) should list the correct classes and methods
- **Graceful degradation**: if you delete the code-graph container docs and re-run a review, the tool should return "No code graph available..."

## Notes

- Incremental reviews only show changes since the last reviewed iteration. Push new commits to trigger a new review.
- The PAT is in `Revu/local.settings.json` under `AzureDevOps__Organizations__ivanradovic__PersonalAccessToken`.
- ADO webhook subscriptions exist for both `pullrequest.created` and `pullrequest.updated` events on `revu-ado`.
