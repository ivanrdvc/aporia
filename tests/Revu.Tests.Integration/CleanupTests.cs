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
        await GitClient.CleanThreads(Target);

        Output.WriteLine($"Cleanup done on PR #{Target.PullRequestId}.");
    }
}
