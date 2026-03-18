using System.ComponentModel.DataAnnotations;

using Microsoft.TeamFoundation.SourceControl.WebApi;

using Aporia.Git;

using Xunit.Abstractions;

namespace Aporia.Tests.Integration.Fixtures;

internal class AdoTestHelper(TestRepoOptions options, GitHttpClient gitClient) : ITestHelper
{
    public ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main") =>
        new(options.Provider, options.Project, options.RepositoryId, options.RepositoryName,
            prId, sourceBranch, targetBranch, options.Organization ?? "");

    public async Task<int> GetAporiaCommentCount(ReviewRequest req)
    {
        var threads = await GetAporiaThreads(req);
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

    public async Task<(int ThreadId, int CommentId)> PostCommentOnAporiaThread(ReviewRequest req, string message)
    {
        var threads = await GetAporiaThreads(req);
        var target = threads.FirstOrDefault(t => t.ThreadContext?.FilePath is not null)
                     ?? throw new InvalidOperationException("No Aporia thread found to post on");

        var comment = new Comment { Content = message, CommentType = CommentType.Text };
        var posted = await gitClient.CreateCommentAsync(
            comment, req.Project, req.RepositoryId, req.PullRequestId, target.Id);
        return (target.Id, posted.Id);
    }

    public async Task<(int ThreadId, int CommentId, string Message)> FindLatestHumanComment(ReviewRequest req)
    {
        var threads = await GetAporiaThreads(req);
        foreach (var thread in threads.OrderByDescending(t => t.Id))
        {
            var human = thread.Comments
                .Where(c => c.Content is not null && !c.IsDeleted)
                .Where(c => !c.Content!.StartsWith("<!-- aporia"))
                .OrderByDescending(c => c.Id)
                .FirstOrDefault();

            if (human is not null)
                return (thread.Id, human.Id, human.Content!);
        }

        throw new InvalidOperationException("No human comment found on any Aporia thread");
    }

    private async Task<List<GitPullRequestCommentThread>> GetAporiaThreads(ReviewRequest req)
    {
        var threads = await gitClient.GetThreadsAsync(
            project: req.Project,
            repositoryId: req.RepositoryId,
            pullRequestId: req.PullRequestId);

        return threads
            .Where(t => t.Properties?.GetValue<string>("aporia:version", null!) is not null)
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

    public long? InstallationId { get; init; }
}
