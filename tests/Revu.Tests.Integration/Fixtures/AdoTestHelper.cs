using System.ComponentModel.DataAnnotations;

using Microsoft.TeamFoundation.SourceControl.WebApi;

using Revu.Git;

using Xunit.Abstractions;

namespace Revu.Tests.Integration.Fixtures;

internal class AdoTestHelper(TestRepoOptions options, GitHttpClient gitClient) : ITestHelper
{
    public ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main") =>
        new(options.Provider, options.Project, options.RepositoryId, options.RepositoryName,
            prId, sourceBranch, targetBranch, options.Organization ?? "");

    public async Task<int> GetRevuCommentCount(ReviewRequest req)
    {
        var threads = await GetRevuThreads(req);
        return threads.Count(t => t.ThreadContext?.FilePath is not null);
    }

    public async Task PrintComments(ReviewRequest req, ITestOutputHelper output)
    {
        var threads = await gitClient.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        var visibleThreads = threads
            .Where(t => t.Comments.Any(c => c.Content is not null))
            .ToList();

        output.WriteLine($"=== PR threads ({visibleThreads.Count}) ===\n");

        foreach (var thread in visibleThreads)
        {
            var ctx = thread.ThreadContext;
            if (ctx?.FilePath is not null)
            {
                var line = ctx.RightFileStart?.Line is not null
                    ? $"L{ctx.RightFileStart.Line}-{ctx.RightFileEnd?.Line ?? ctx.RightFileStart.Line}"
                    : "";
                output.WriteLine($"--- {ctx.FilePath} {line} [{thread.Status}] ---");
            }
            else
            {
                output.WriteLine($"--- (general) [{thread.Status}] ---");
            }

            foreach (var comment in thread.Comments.Where(c => c.Content is not null))
                output.WriteLine(comment.Content);

            output.WriteLine("");
        }
    }

    private async Task<List<GitPullRequestCommentThread>> GetRevuThreads(ReviewRequest req)
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
    [Required]
    public string Project { get; init; } = string.Empty;

    [Required]
    public string RepositoryId { get; init; } = string.Empty;

    [Required]
    public string RepositoryName { get; init; } = string.Empty;

    public GitProvider Provider { get; init; } = GitProvider.Ado;

    public string? Organization { get; init; }
}
