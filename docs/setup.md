# Azure Setup

```bash
az login
az group create -n rg-revu-prod -l <region>
az ad sp create-for-rbac --name revu-deploy --role contributor \
  --scopes /subscriptions/<sub-id>/resourceGroups/rg-revu-prod --sdk-auth
```

Add the JSON output as `AZURE_CREDENTIALS` in GitHub (Settings → Environments → `prod`).
Also add: `AI_OPENAI_KEY`, `AI_ANTHROPIC_KEY`. Push to `main` deploys automatically.

## Adding a repository

### 1. Add org credentials (if new org)

```bash
az functionapp config appsettings set -n func-revu -g rg-revu-prod --settings \
  "AzureDevOps__Organizations__<org>__Organization=<org>" \
  "AzureDevOps__Organizations__<org>__PersonalAccessToken=<pat>"
```

### 2. Register the repo

```bash
curl -X POST "https://func-revu.azurewebsites.net/api/manage/repos?code=<function-key>" \
  -H "Content-Type: application/json" \
  -d '{"repositoryId":"<repo-id>","provider":"ado","name":"<repo-name>","organization":"<org>"}'
```

Get the function key: `az functionapp keys list -n func-revu -g rg-revu-prod`

### 3. Set up the webhook in ADO

**Project Settings → Service Hooks → Web Hooks → Pull request created/updated**:

```
https://func-revu.azurewebsites.net/api/webhook/ado?code=<function-key>
```
