using System.Collections.Concurrent;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

using Aporia.Git;

namespace Aporia.DocWatch;

/// <summary>
/// Azure DevOps implementation of <see cref="IDocPublisher"/>. Uses the ADO Git SDK
/// to publish documentation changes via pushes and pull requests.
/// </summary>
public class AdoDocPublisher(IOptions<AdoOptions> adoOptions) : IDocPublisher
{
    private readonly ConcurrentDictionary<string, GitHttpClient> _gitClients = [];

    private GitHttpClient GetGitClient(string org) =>
        _gitClients.GetOrAdd(org, key =>
        {
            var config = adoOptions.Value.Organizations[key];
            var connection = new VssConnection(
                new Uri($"https://dev.azure.com/{config.Organization}"),
                new VssBasicCredential(string.Empty, config.PersonalAccessToken));
            return connection.GetClient<GitHttpClient>();
        });

    public async Task<string> CreateBranch(string repoId, string baseBranch, string newBranch, long? installationId = null)
    {
        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var baseBranchRef = baseBranch.StartsWith("refs/") ? baseBranch : $"refs/heads/{baseBranch}";
        var newBranchRef = newBranch.StartsWith("refs/") ? newBranch : $"refs/heads/{newBranch}";

        var refs = await git.GetRefsAsync(project, repo, filter: baseBranchRef.Replace("refs/", ""));
        var baseRef = refs.FirstOrDefault()
            ?? throw new InvalidOperationException($"Branch {baseBranch} not found in {repoId}");

        var refUpdate = new GitRefUpdate
        {
            Name = newBranchRef,
            OldObjectId = "0000000000000000000000000000000000000000",
            NewObjectId = baseRef.ObjectId
        };

        await git.UpdateRefsAsync([refUpdate], repositoryId: repo, project: project);

        return baseRef.ObjectId;
    }

    public async Task PushFiles(string repoId, string branch, IReadOnlyList<DocFile> files, string commitMessage, long? installationId = null)
    {
        if (files.Count == 0) return;

        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var branchRef = branch.StartsWith("refs/") ? branch : $"refs/heads/{branch}";

        var refs = await git.GetRefsAsync(project, repo, filter: branchRef.Replace("refs/", ""));
        var headRef = refs.FirstOrDefault()
            ?? throw new InvalidOperationException($"Branch {branch} not found in {repoId}");

        // Check which files already exist so we use the correct ChangeType per file
        var existingItems = await git.GetItemsAsync(project, repo,
            scopePath: "/", recursionLevel: VersionControlRecursionType.Full,
            versionDescriptor: new GitVersionDescriptor { Version = branch, VersionType = GitVersionType.Branch });
        var existingPaths = new HashSet<string>(existingItems.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        var changes = files.Select(f =>
        {
            var path = "/" + f.Path.TrimStart('/');
            return new GitChange
            {
                ChangeType = existingPaths.Contains(path) ? VersionControlChangeType.Edit : VersionControlChangeType.Add,
                Item = new GitItem { Path = path },
                NewContent = new ItemContent { Content = f.Content, ContentType = ItemContentType.RawText }
            };
        }).ToList();

        var push = new GitPush
        {
            RefUpdates = [new GitRefUpdate { Name = branchRef, OldObjectId = headRef.ObjectId }],
            Commits = [new GitCommitRef { Comment = commitMessage, Changes = changes }]
        };

        await git.CreatePushAsync(push, project: project, repositoryId: repo);
    }

    public async Task<int> CreatePullRequest(string repoId, string sourceBranch, string targetBranch, string title, string body, long? installationId = null)
    {
        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var sourceRef = sourceBranch.StartsWith("refs/") ? sourceBranch : $"refs/heads/{sourceBranch}";
        var targetRef = targetBranch.StartsWith("refs/") ? targetBranch : $"refs/heads/{targetBranch}";

        var pr = await git.CreatePullRequestAsync(
            new GitPullRequest
            {
                SourceRefName = sourceRef,
                TargetRefName = targetRef,
                Title = title,
                Description = body
            },
            project: project,
            repositoryId: repo);

        return pr.PullRequestId;
    }

    public async Task UpdatePullRequest(string repoId, int prNumber, string? title = null, string? body = null, long? installationId = null)
    {
        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var update = new GitPullRequest();
        if (title is not null) update.Title = title;
        if (body is not null) update.Description = body;

        await git.UpdatePullRequestAsync(update, project: project, repositoryId: repo, pullRequestId: prNumber);
    }

    public async Task<(int Number, string Branch, string Body)?> FindOpenPullRequest(string repoId, string label, long? installationId = null)
    {
        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var prs = await git.GetPullRequestsAsync(
            project: project,
            repositoryId: repo,
            new GitPullRequestSearchCriteria
            {
                Status = PullRequestStatus.Active,
                SourceRefName = $"refs/heads/{DocWatchConstants.BranchName}"
            });

        var match = prs.FirstOrDefault();
        if (match is null) return null;

        var branch = match.SourceRefName.Replace("refs/heads/", "");
        return (match.PullRequestId, branch, match.Description ?? "");
    }

    public async Task AddComment(string repoId, int prNumber, string body, long? installationId = null)
    {
        var (org, project, repo) = ParseRepoId(repoId);
        var git = GetGitClient(org);

        var thread = new GitPullRequestCommentThread
        {
            Comments = [new Comment { Content = body }]
        };

        await git.CreateThreadAsync(thread, project: project, repositoryId: repo, pullRequestId: prNumber);
    }

    private static (string Org, string Project, string Repo) ParseRepoId(string repoId)
    {
        // ADO repoIds are stored as the repo GUID, but we need org context.
        // The org is passed separately via the doc watch project registration.
        // For now, assume repoId format: "org__project__repoId" for doc watch.
        var parts = repoId.Split("__");
        return parts.Length >= 3
            ? (parts[0], parts[1], parts[2])
            : throw new ArgumentException($"Invalid ADO repoId format: {repoId}. Expected 'org__project__repoId'.");
    }
}
