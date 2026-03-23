using Aporia.Git;

namespace Aporia.Tests.Integration.Fixtures;

internal static class Scenarios
{
    // --- ADO scenarios (ivanrdvc/eShop repo) ---

    // 19 files, multi-agent. Order tracking & shipping notifications feature.
    //   Required (expect reviewer to catch):
    //   - Inconsistent HttpClient: ShippingService uses new HttpClient() instead of factory
    //   - Cross-service DB: ShippingNotificationService queries catalogdb directly
    //   - Retry non-idempotent: NotificationDispatcher retries POST that creates duplicates
    //   - Missing cancellation token: UpdateTrackingAsync doesn't propagate CancellationToken
    //   Expected (should catch with tools):
    //   - DI scope leak: OrderTrackingCache (singleton) captures IOrderRepository (scoped)
    //   - Domain logic in infra: OrderRepository.GetShippableOrdersAsync has business rules
    //   Stretch:
    //   - Semantic version skew: Catalog price changed to tax-exclusive, OrderProcessor still treats as inclusive
    public static ReviewRequest AdoMultiAgentCrossService =>
        EShopAdoRequest(53, "refs/heads/feature/order-tracking-notifications");

    // --- GitHub scenarios (ivanrdvc/eShop fork) ---

    // Same planted bugs as ADO, different provider.
    public static ReviewRequest GitHubMultiAgentCrossService => new(
        Provider: GitProvider.GitHub,
        Project: "",
        RepositoryId: "ivanrdvc__eShop",
        RepositoryName: "eShop",
        PullRequestId: 1,
        SourceBranch: "refs/heads/feature/order-tracking-notifications",
        TargetBranch: "refs/heads/main",
        Organization: "ivanrdvc");

    private static ReviewRequest EShopAdoRequest(int prId, string sourceBranch) => new(
        Provider: GitProvider.Ado,
        Project: "ivanrdvc",
        RepositoryId: "a9f4a02d-bc66-4fb6-8009-a8ea2b713e6e",
        RepositoryName: "eShop",
        PullRequestId: prId,
        SourceBranch: sourceBranch,
        TargetBranch: "refs/heads/main",
        Organization: "ivanradovic");
}
