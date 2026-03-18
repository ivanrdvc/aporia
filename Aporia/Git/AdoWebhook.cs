namespace Aporia.Git;

/// <summary>
/// ADO service hook payload for <c>git.pullrequest.created</c> / <c>git.pullrequest.updated</c>.
/// </summary>
public record AdoWebhook(string EventType, AdoPullRequest Resource)
{
    public ReviewRequest? ToRequest() =>
        EventType is "git.pullrequest.created" or "git.pullrequest.updated" && !Resource.IsDraft
            ? new ReviewRequest(
                Provider: GitProvider.Ado,
                Project: Resource.Repository.Project.Name,
                RepositoryId: Resource.Repository.Id,
                RepositoryName: Resource.Repository.Name,
                PullRequestId: Resource.PullRequestId,
                SourceBranch: Resource.SourceRefName,
                TargetBranch: Resource.TargetRefName)
            : null;
}

public record AdoPullRequest(
    int PullRequestId,
    string SourceRefName,
    string TargetRefName,
    bool IsDraft,
    AdoRepository Repository
);

public record AdoRepository(string Id, string Name, AdoProject Project);
public record AdoProject(string Name);
