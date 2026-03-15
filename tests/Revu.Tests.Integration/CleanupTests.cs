using Revu.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Revu.Tests.Integration;

public class CleanupTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private static readonly ReviewRequest Target = Scenarios.MultiAgentCrossService;

    [Fact]
    public async Task DeleteAllComments()
    {
        await ResetReviewState(Target);
        await TestHelper.CleanComments(Target);
        Output.WriteLine($"Cleanup done on PR #{Target.PullRequestId}.");
    }

    [Fact]
    public async Task DeleteAllComments_SelfReview()
    {
        await ResetReviewState(Scenarios.SelfReview);
        await TestHelper.CleanComments(Scenarios.SelfReview);
        Output.WriteLine($"Cleanup done on PR #{Scenarios.SelfReview.PullRequestId}.");
    }
}
