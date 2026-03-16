using Microsoft.Extensions.DependencyInjection;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Review;

using Xunit.Abstractions;

namespace Revu.Tests.Integration.Fixtures;

/// <summary>
/// Base class for integration tests — manages a DI scope per test and exposes
/// commonly used services as properties. Dispose cleans up the scope automatically.
/// </summary>
[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTestBase(
    AppFixture fixture,
    ITestOutputHelper output) : IDisposable
{
    private readonly IServiceScope _scope = fixture.Services.CreateScope();
    protected IServiceProvider Services => _scope.ServiceProvider;

    protected ITestOutputHelper Output => output;
    protected string SessionDirectory => fixture.SessionDirectory;
    protected IGitConnector Git => Services.GetRequiredService<IGitConnector>();
    protected ITestHelper TestHelper => Services.GetRequiredService<ITestHelper>();
    protected Reviewer Reviewer => Services.GetRequiredService<Reviewer>();
    protected IReviewStore ReviewStore => Services.GetRequiredService<IReviewStore>();

    /// <summary>Reset iteration state so GetDiff returns a full diff.</summary>
    protected async Task ResetReviewState(ReviewRequest req)
    {
        await Services.GetRequiredService<IPrStateStore>()
            .SaveAsync(req.RepositoryId, req.PullRequestId, "0");
    }

    /// <summary>Fetch and print all visible PR comments to test output.</summary>
    protected Task PrintThreads(ReviewRequest req) =>
        TestHelper.PrintComments(req, Output);

    public void Dispose() => _scope.Dispose();
}

[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "Integration";
}
