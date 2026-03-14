using Revu.Git;

namespace Revu.Tests.Integration.Fixtures;

internal static class Scenarios
{
    // 10 files, single-agent. Also used as the eval baseline.
    public static ReviewRequest SingleAgentBaseline =>
        AdoThreadHelper.PrRequest(11, "refs/heads/feature/order-discounts");

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
        AdoThreadHelper.PrRequest(12, "refs/heads/feature/order-tracking-notifications");

    // 2 iterations: iteration 1 has two bugs, iteration 2 fixes one.
    public static ReviewRequest IncrementalTest =>
        AdoThreadHelper.PrRequest(8, "refs/heads/feature/incremental-test");

    // Self-review: Revu reviews its own code on the revu-ado mirror repo.
    public static ReviewRequest SelfReview => new(
        Provider: GitProvider.Ado,
        Project: "ivanrndvc-sc",
        RepositoryId: "4421a1a1-a185-4273-b7a9-58585a559fb1",
        RepositoryName: "revu-ado",
        PullRequestId: 18,
        SourceBranch: "refs/heads/feat/code-graph",
        TargetBranch: "refs/heads/main",
        Organization: "ivanradovic");
}
