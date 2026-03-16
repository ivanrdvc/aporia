# Azure Setup

```bash
az login
az group create -n rg-revu-prod -l <region>
az ad sp create-for-rbac --name revu-deploy --role contributor \
  --scopes /subscriptions/<sub-id>/resourceGroups/rg-revu-prod --sdk-auth
```

Add the JSON output as `AZURE_CREDENTIALS` in GitHub (Settings → Environments → `prod`).
Also add: `AI_ANTHROPIC_KEY`. Push to `main` deploys automatically.

## Adding a repository

### ADO

#### 1. Add org credentials (if new org)

```bash
az functionapp config appsettings set -n func-revu -g rg-revu-prod --settings \
  "AzureDevOps__Organizations__<org>__Organization=<org>" \
  "AzureDevOps__Organizations__<org>__PersonalAccessToken=<pat>"
```

#### 2. Register the repo

```bash
curl -X POST "https://func-revu.azurewebsites.net/api/manage/repos?code=<function-key>" \
  -H "Content-Type: application/json" \
  -d '{"repositoryId":"<repo-id>","provider":"ado","name":"<repo-name>","organization":"<org>"}'
```

Get the function key: `az functionapp keys list -n func-revu -g rg-revu-prod`

#### 3. Set up webhooks in ADO

**Project Settings → Service Hooks → Web Hooks**:

| Event | URL |
|---|---|
| Pull request created | `https://func-revu.azurewebsites.net/api/webhook/ado?code=<function-key>` |
| Pull request updated | `https://func-revu.azurewebsites.net/api/webhook/ado?code=<function-key>` |
| Pull request commented on | `https://func-revu.azurewebsites.net/api/webhook/ado/comment?code=<function-key>` |

The first two trigger reviews. The third enables [PR chat](features/pr-chat.md) — replies on
Revu threads and `@revu` mentions.

### GitHub

#### 1. Add org credentials (if new org)

```bash
az functionapp config appsettings set -n func-revu -g rg-revu-prod --settings \
  "GitHub__Organizations__<key>__Token=<pat>"
```

For local dev / integration tests:

```bash
dotnet user-secrets set "GitHub:Organizations:<key>:Token" "<pat>" --project Revu/Revu.csproj
dotnet user-secrets set "GitHub:Organizations:<key>:Token" "<pat>" --project tests/Revu.Tests.Integration/Revu.Tests.Integration.csproj
```

PAT needs `repo` scope. Or use `gh auth token` if already authenticated via GitHub CLI.

#### 2. Register the repo

```bash
curl -X POST "https://func-revu.azurewebsites.net/api/manage/repos?code=<function-key>" \
  -H "Content-Type: application/json" \
  -d '{"repositoryId":"<owner>/<repo>","provider":"github","name":"<repo>","organization":"<owner>"}'
```

#### 3. Set up the webhook in GitHub

**Repo Settings → Webhooks → Add webhook**:

- Payload URL: `https://func-revu.azurewebsites.net/api/webhook/github?code=<function-key>`
- Content type: `application/json`
- Secret: the value of `GitHub:WebhookSecret` app setting
- Events: select "Pull requests" only
