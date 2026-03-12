using Microsoft.Extensions.DependencyInjection;
using Microsoft.TeamFoundation.SourceControl.WebApi;

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
    private IServiceProvider Services => _scope.ServiceProvider;

    protected ITestOutputHelper Output => output;
    protected string SessionDirectory => fixture.SessionDirectory;
    protected IGitConnector Git => Services.GetRequiredService<IGitConnector>();
    protected GitHttpClient GitClient => Services.GetRequiredService<GitHttpClient>();
    protected Reviewer Reviewer => Services.GetRequiredService<Reviewer>();

    /// <summary>Reset iteration state so GetDiff returns a full diff.</summary>
    protected async Task ResetReviewState(ReviewRequest req)
    {
        await Services.GetRequiredService<IPrStateStore>()
            .SaveAsync(req.RepositoryId, req.PullRequestId, 0);
    }

    /// <summary>Fetch and print all visible PR threads to test output.</summary>
    protected async Task PrintThreads(ReviewRequest req)
    {
        var threads = await GitClient.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        var visibleThreads = threads
            .Where(t => t.Comments.Any(c => c.Content is not null))
            .ToList();

        Output.WriteLine($"=== PR threads ({visibleThreads.Count}) ===\n");

        foreach (var thread in visibleThreads)
        {
            var ctx = thread.ThreadContext;
            if (ctx?.FilePath is not null)
            {
                var line = ctx.RightFileStart?.Line is not null
                    ? $"L{ctx.RightFileStart.Line}-{ctx.RightFileEnd?.Line ?? ctx.RightFileStart.Line}"
                    : "";
                Output.WriteLine($"--- {ctx.FilePath} {line} [{thread.Status}] ---");
            }
            else
            {
                Output.WriteLine($"--- (general) [{thread.Status}] ---");
            }

            foreach (var comment in thread.Comments.Where(c => c.Content is not null))
                Output.WriteLine(comment.Content);

            Output.WriteLine("");
        }
    }

    public void Dispose() => _scope.Dispose();
}

[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "Integration";
}
