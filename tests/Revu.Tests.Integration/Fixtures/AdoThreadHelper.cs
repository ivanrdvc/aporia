using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;

using Revu.Git;

namespace Revu.Tests.Integration.Fixtures;

/// <summary>
/// Shared test repo coordinates and PR thread helpers for integration tests.
/// Values are loaded from the "TestRepo" section in appsettings.test.json.
/// </summary>
internal static class AdoThreadHelper
{
    private static TestRepoOptions _options = null!;

    public static void Initialize(IOptions<TestRepoOptions> options) =>
        _options = options.Value;

    public static ReviewRequest PrRequest(int pullRequestId, string sourceBranch, string targetBranch = "refs/heads/main") =>
        new(_options.Provider, _options.Project, _options.RepositoryId, _options.RepositoryName, pullRequestId, sourceBranch, targetBranch, _options.Organization ?? "");

    /// <summary>
    /// Delete all comments on a PR — used for test cleanup.
    /// </summary>
    public static async Task CleanThreads(this GitHttpClient gitClient, ReviewRequest req)
    {
        var threads = await gitClient.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        foreach (var thread in threads)
        {
            if (thread.Id <= 0 || thread.Comments is null)
                continue;

            foreach (var comment in thread.Comments)
            {
                if (comment.Id <= 0 || comment.IsDeleted)
                    continue;

                await gitClient.DeleteCommentAsync(
                    repositoryId: req.RepositoryId,
                    pullRequestId: req.PullRequestId,
                    threadId: thread.Id,
                    commentId: comment.Id,
                    project: req.Project);
            }
        }
    }

    /// <summary>
    /// Fetch threads tagged with revu:version that have visible comments.
    /// </summary>
    public static async Task<List<GitPullRequestCommentThread>> GetRevuThreads(this GitHttpClient gitClient, ReviewRequest req)
    {
        var threads = await gitClient.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        return threads
            .Where(t => t.Properties?.GetValue<string>("revu:version", null!) is not null)
            .Where(t => t.Comments.Any(c => c.Content is not null && !c.IsDeleted))
            .ToList();
    }
}

public class TestRepoOptions
{
    public const string SectionName = "TestRepo";

    [Required]
    public string Project { get; init; } = string.Empty;

    [Required]
    public string RepositoryId { get; init; } = string.Empty;

    [Required]
    public string RepositoryName { get; init; } = string.Empty;

    public GitProvider Provider { get; init; } = GitProvider.Ado;

    public string? Organization { get; init; }
}
