---
date: 2026-03-17
status: draft
tags: [github, auth, identity]
---

# GitHub App Identity for Aporia

## Problem Statement

Aporia posts review comments using a personal access token (PAT), so all comments appear as the PAT
owner's personal GitHub account. This is confusing for PR authors ("is this Ivan or the bot?"),
makes it harder to filter/mute bot comments, and ties the service to a personal credential that
must be manually rotated. ADO has the same PAT-as-person problem but lacks a first-party "App"
equivalent.

## Decision Drivers

- Comments should clearly come from a bot identity (`aporia[bot]`), not a person
- Short-lived, auto-rotated tokens are preferred over long-lived PATs
- Must support per-repo installation (org admins can control which repos Aporia accesses)
- ADO doesn't have a GitHub App equivalent — service principal or dedicated account are the options
- Existing PAT flow must remain as fallback during migration and for ADO

## Solution

**GitHub:** Register a GitHub App. Auth switches from a static PAT to: App private key →
installation access token (short-lived, 1hr expiry, auto-rotated). Comments post as `aporia[bot]`.

**ADO:** Create a dedicated service account (`aporia-bot@`) with a scoped PAT (Code Read/Write).
Comments post as that account. Service principal via Entra ID is the longer-term path but requires
Entra admin access.

## Research Summary

**Current auth flow (GitHub):**
- `GitHubOptions.Organizations[<key>].Token` holds a static PAT
- `GitHubConnector` (line 386) sets `Authorization: Bearer <token>` on all HTTP requests
- Webhook validation uses HMAC-SHA256 via `GitHubOptions.WebhookSecret`
- Comments posted via `POST /repos/{owner}/{repo}/pulls/{prNumber}/reviews` (batched in groups of 30)

**Current auth flow (ADO):**
- `AdoOptions.Organizations[<key>].PersonalAccessToken` holds a static PAT
- `AdoConnector` uses `VssBasicCredential(string.Empty, pat)` for SDK clients (line 38)
- HTTP Basic auth (`:<pat>` base64) for the search API (lines 50-52)

**GitHub App auth model:**
- App has `AppId` + `PrivateKey` (RSA PEM)
- Generate a JWT signed with the private key (10min expiry)
- Exchange JWT for an installation access token via `POST /app/installations/{id}/access_tokens`
- Installation token is scoped to the repos the app is installed on, expires in 1hr
- Required permissions: `pull_requests: write`, `contents: read`, `issues: write` (for summary comments)
- Webhook events: `pull_request`, `issue_comment` (for chat)

**How other bots do it:**
- **Qodo PR-Agent** (Python): PyGithub's `Auth.AppAuth` handles JWT + token exchange. Token
  auto-refreshes 20s before expiry. Permissions: `pull_requests: rw`, `issues: rw`, `contents: r`,
  `metadata: r`.
- **Octokit.js `@octokit/auth-app`**: LRU cache with 59-min TTL (GitHub tokens expire at 60min).
  Concurrent requests for the same installation share a single pending promise.
- **NearForm Reviewbot**: App manifest adds `pull_request_review` and
  `pull_request_review_comment` events beyond the basics.

**.NET JWT approach (no external crypto deps):**
```csharp
using var rsa = RSA.Create();
rsa.ImportFromPem(privateKeyPem);  // built-in since .NET 5

var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
var now = DateTime.UtcNow.AddSeconds(-60); // clock drift buffer

var token = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false }
    .CreateToken(new SecurityTokenDescriptor
    {
        Issuer = appId.ToString(),
        IssuedAt = now,
        Expires = now.AddMinutes(10),
        SigningCredentials = credentials
    });
var jwt = new JwtSecurityTokenHandler().WriteToken(token);
// Then POST /app/installations/{id}/access_tokens with Bearer {jwt}
```

Uses `RSA.ImportFromPem()` (built-in .NET 5+) + `System.IdentityModel.Tokens.Jwt`. No
BouncyCastle, no GitHubJwt NuGet needed. Clean for a .NET 10 project.

**Coexistence model:** GitHub App and ADO PAT run side-by-side with zero conflict.
`GitHubConnector` and `AdoConnector` resolve auth independently from `GitHubOptions` and
`AdoOptions`. Provider is selected per-request from `ReviewRequest.Provider`.

**Key files:**
- `Aporia/Git/GitHubOptions.cs` — token config (lines 14-17)
- `Aporia/Git/GitHubConnector.cs` — HTTP client, auth header, PostReview (lines 167-281, 386)
- `Aporia/Git/AdoOptions.cs` — PAT config (lines 14-17)
- `Aporia/Git/AdoConnector.cs` — VssBasicCredential, PostReview (lines 38, 194-258)
- `Aporia/Functions/WebhookFunction.cs` — webhook validation (lines 60-80)
- `Aporia/Program.cs` — options binding (lines 26-27)

## Implementation Steps

1. **Register the GitHub App**
   - Create at github.com/settings/apps (or org settings for org-owned)
   - Permissions: `pull_requests: write`, `contents: read`, `issues: write`
   - Subscribe to webhook event: `pull_request`
   - Generate and download private key (PEM file)
   - Note the App ID and Installation ID

2. **Extend `GitHubOptions` with App credentials**
   - Files: `Aporia/Git/GitHubOptions.cs`
   - Add `AppId` (long), `PrivateKey` (string, PEM), `InstallationId` (long) alongside existing `Token`
   - Keep `Token` as optional fallback for PAT-based auth

3. **Add GitHub App token provider**
   - Files: new `Aporia/Git/GitHubAppTokenProvider.cs`
   - JWT generation: `RSA.ImportFromPem()` + `System.IdentityModel.Tokens.Jwt` (no external crypto deps)
   - 60-second clock drift buffer on `iat` (matches GitHub docs recommendation)
   - Token exchange: `POST /app/installations/{id}/access_tokens` with `Bearer {jwt}`
   - Cache installation token in-memory with ~55min TTL (tokens expire at 60min)
   - Interface: `Task<string> GetTokenAsync()` — returns a valid bearer token
   - Register as singleton (token is cached, thread-safe)
   - NuGet: `System.IdentityModel.Tokens.Jwt` + `Microsoft.IdentityModel.Tokens`

4. **Update `GitHubConnector` to use token provider**
   - Files: `Aporia/Git/GitHubConnector.cs`
   - Replace static `Bearer <token>` header with `await tokenProvider.GetTokenAsync()`
   - If `AppId` is configured, use App flow; otherwise fall back to PAT
   - `User-Agent` header should identify the app (GitHub requires this for App API calls)

5. **Update webhook validation**
   - Files: `Aporia/Functions/WebhookFunction.cs`
   - GitHub App webhooks use the same HMAC-SHA256 mechanism — existing validation works
   - Webhook secret moves from standalone config to the App's webhook secret

6. **Update configuration and secrets**
   - Files: `Aporia/Program.cs`, `Aporia/local.settings.example.json`
   - Bind new App fields: `GitHub__AppId`, `GitHub__PrivateKey`, `GitHub__InstallationId`
   - Private key stored in Azure Key Vault (referenced via app setting), not inline

7. **ADO: Create dedicated service account**
   - No code changes — just use a different PAT from a `aporia-bot@` account
   - Update deployment config to use the bot account's PAT

## Open Questions

- [ ] Org-owned App vs personal App? Org-owned is better for team visibility but requires org admin
- [ ] Multi-installation support? Current model is one installation per config key. If Aporia serves multiple orgs, need installation ID resolution from webhook payload (`installation.id`)
- [ ] Should the private key be stored as a Key Vault secret or as a certificate? PEM string in Key Vault is simplest
- [ ] ADO: is a service principal (Entra ID) worth pursuing now, or is a bot account sufficient for the near term?
