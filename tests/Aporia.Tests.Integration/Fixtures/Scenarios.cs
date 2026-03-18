using Aporia.Git;

namespace Aporia.Tests.Integration.Fixtures;

internal static class Scenarios
{
    // --- ADO scenarios (ivanrndvc-sc repo) ---

    // 10 files, single-agent. Also used as the eval baseline.
    public static ReviewRequest SingleAgentBaseline =>
        AdoRequest(11, "refs/heads/feature/order-discounts");

    // 15 files across 4 services, multi-agent.
    //   Easy (visible in diff):
    //   - PII logging: ShippingNotificationService logs customer email + full address
    //   - Enum ordinal shift: WebhookType inserts value 2, shifting OrderShipped 2→3, OrderPaid 3→4
    //   Hard (require cross-file investigation):
    //   - DDD violation: SetTrackingNumber allows any status (compare SetShippedStatus guard)
    //   - Cross-service DB: ShippingRatesApi queries ordering DB from Catalog.API
    //   - Cross-service DB: GracePeriodManagerService queries catalog DB from OrderProcessor
    //   - Inconsistent handler: OrderTrackingUpdatedHandler uses manual HttpClient, not retriever/sender
    //   - IDOR: UpdateTrackingCommandHandler doesn't verify caller owns the order
    public static ReviewRequest MultiAgentCrossService =>
        AdoRequest(12, "refs/heads/feature/order-tracking-notifications");

    // 2 iterations: iteration 1 has two bugs, iteration 2 fixes one.
    public static ReviewRequest IncrementalTest =>
        AdoRequest(8, "refs/heads/feature/incremental-test");

    // Self-review: Aporia reviews its own code on the aporia-ado mirror repo.
    public static ReviewRequest SelfReview => new(
        Provider: GitProvider.Ado,
        Project: "ivanrndvc-sc",
        RepositoryId: "e7e78266-9f1a-4b5a-9fdb-825bf2c6f293",
        RepositoryName: "aporia-ado",
        PullRequestId: 18,
        SourceBranch: "refs/heads/feat/code-graph",
        TargetBranch: "refs/heads/main",
        Organization: "ivanradovic");

    // --- GitHub scenarios (ivanrdvc/eShop fork) ---

    // Same 15 files / same planted bugs as ADO MultiAgentCrossService (PR 12).
    public static ReviewRequest GitHubMultiAgentCrossService => new(
        Provider: GitProvider.GitHub,
        Project: "",
        RepositoryId: "ivanrdvc/eShop",
        RepositoryName: "eShop",
        PullRequestId: 1,
        SourceBranch: "refs/heads/feature/order-tracking-notifications",
        TargetBranch: "refs/heads/main",
        Organization: "ivanrdvc");

    private static ReviewRequest AdoRequest(int prId, string sourceBranch) => new(
        Provider: GitProvider.Ado,
        Project: "ivanrndvc-sc",
        RepositoryId: "068b4389-3bae-438c-a0a7-08619db2b998",
        RepositoryName: "ivanrndvc-sc",
        PullRequestId: prId,
        SourceBranch: sourceBranch,
        TargetBranch: "refs/heads/main",
        Organization: "ivanradovic");
}
