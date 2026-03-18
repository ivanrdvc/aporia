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

Two auth modes: **GitHub App** (recommended — comments as `revu[bot]`, auto-rotated tokens) or
**PAT** (fallback — comments as personal account).

#### Option A: GitHub App (recommended)

1. **Create the app** — GitHub Settings → Developer settings → GitHub Apps → New
   - Webhook URL: `https://func-revu.azurewebsites.net/api/webhook/github?code=<function-key>`
   - Permissions: `pull_requests: rw`, `contents: r`, `issues: rw`, `metadata: r`
   - Events: Pull request
   - Generate and save a webhook secret
2. **Generate a private key** — on the app page, download the `.pem` file
3. **Install the app** on your org/account (all repos or selected)
4. **Configure Revu:**

```bash
# Production
az functionapp config appsettings set -n func-revu -g rg-revu-prod --settings \
  "GitHub__AppId=<app-id>" \
  "GitHub__PrivateKey=@path/to/private-key.pem" \
  "GitHub__WebhookSecret=<webhook-secret>"

# Private key via Key Vault (recommended for prod):
# GitHub__PrivateKey=@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/github-app-key)

# Local dev
dotnet user-secrets set "GitHub:AppId" "<app-id>" --project Revu/Revu.csproj
dotnet user-secrets set "GitHub:PrivateKey" "$(cat private-key.pem)" --project Revu/Revu.csproj
dotnet user-secrets set "GitHub:WebhookSecret" "<secret>" --project Revu/Revu.csproj
```

5. **Register each repo** (the App handles webhook routing — no per-repo webhook setup needed):

```bash
curl -X POST "https://func-revu.azurewebsites.net/api/manage/repos?code=<function-key>" \
  -H "Content-Type: application/json" \
  -d '{"repositoryId":"<owner>/<repo>","provider":"github","name":"<repo>","organization":"<owner>"}'
```

#### Option B: PAT (fallback)

```bash
az functionapp config appsettings set -n func-revu -g rg-revu-prod --settings \
  "GitHub__Token=<pat>" \
  "GitHub__WebhookSecret=<webhook-secret>"
```

PAT needs `repo` scope. Register the repo (same as above), then add a webhook on each repo:
Repo Settings → Webhooks → Payload URL `https://func-revu.azurewebsites.net/api/webhook/github?code=<function-key>`,
content type `application/json`, secret = `GitHub:WebhookSecret`, events = "Pull requests".
