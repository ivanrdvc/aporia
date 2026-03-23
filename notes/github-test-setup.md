# Test Repository Setup

## Test Repos

Both providers use the same slimmed-down `dotnet/eShop` fork with identical planted bugs.

### GitHub
- **Repo:** `ivanrdvc/eShop` (fork of `dotnet/eShop`, slimmed to Ordering + Catalog + OrderProcessor)
- **PR #1:** `feature/order-tracking-notifications` → `main`
- **Profile:** `gh-eshop` in `appsettings.test.json`

### ADO
- **Org:** `ivanradovic`, **Project:** `ivanrdvc`
- **Repo:** `eShop` (mirror of GitHub fork)
- **PR #53:** `feature/order-tracking-notifications` → `main`
- **Profile:** `ado-eshop` in `appsettings.test.json`

## Planted Bugs (7 total)

### Required (4) — expect reviewer to catch

| # | Bug | File |
|---|-----|------|
| 1 | Inconsistent HttpClient (`new HttpClient()`) | `Ordering.API/Infrastructure/Services/ShippingService.cs` |
| 2 | Cross-microservice DB access (queries catalogdb) | `OrderProcessor/Services/ShippingNotificationService.cs` |
| 3 | Retry non-idempotent POST | `OrderProcessor/Services/NotificationDispatcher.cs` |
| 4 | Missing cancellation token propagation | `Ordering.API/Apis/OrdersApi.cs` (UpdateTrackingAsync) |

### Expected (2) — should catch with tools

| # | Bug | File |
|---|-----|------|
| 5 | DI scope leak (singleton captures scoped) | `Ordering.API/Infrastructure/Services/OrderTrackingCache.cs` |
| 6 | Domain logic in infrastructure | `Ordering.Infrastructure/Repositories/OrderRepository.cs` |

### Stretch (1) — not expected to catch

| # | Bug | File |
|---|-----|------|
| 7 | Semantic version skew (tax-exclusive price) | `Catalog.API/.../ProductPriceChangedIntegrationEvent.cs` + `OrderProcessor/Services/ProductPriceChangedHandler.cs` |

## Secrets

Both `Aporia.csproj` and `Aporia.Tests.Integration.csproj` user-secrets:

```
GitHub:Token = <gh-pat>
GitHub:AppId = <app-id>
GitHub:PrivateKey = <pem-contents>
AzureDevOps:Organizations:ivanradovic:PersonalAccessToken = <ado-pat>
```

## Config Switching

Flip `TestProfile` in `appsettings.test.json`:
- `"gh-eshop"` — GitHub provider
- `"ado-eshop"` — ADO provider

## Expectations

`expectations.json` keys:
- `gh:ivanrdvc/eShop:1` — GitHub PR 1
- `53` — ADO PR 53
