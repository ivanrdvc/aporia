using System.Text.Json.Serialization;

namespace Aporia.Git;

public record GitHubWebhook(
    string Action,
    [property: JsonPropertyName("pull_request")] GitHubPullRequest PullRequest,
    GitHubRepository Repository,
    GitHubInstallation? Installation = null)
{
    public ReviewRequest? ToRequest() =>
        Action is ("opened" or "synchronize" or "reopened" or "ready_for_review")
            && !PullRequest.Draft
            ? new ReviewRequest(
                Provider: GitProvider.GitHub,
                Project: Repository.Owner.Login,
                RepositoryId: Repository.FullName.Replace("/", "__"),
                RepositoryName: Repository.Name,
                PullRequestId: PullRequest.Number,
                SourceBranch: PullRequest.Head.Ref,
                TargetBranch: PullRequest.Base.Ref,
                InstallationId: Installation?.Id)
            : null;
}

public record GitHubInstallation(long Id);

public record GitHubPullRequest(
    int Number,
    bool Draft,
    GitHubRef Head,
    GitHubRef Base,
    string? Title = null,
    bool Merged = false);

public record GitHubRef(
    string Ref,
    string Sha);

public record GitHubRepository(
    string Name,
    [property: JsonPropertyName("full_name")] string FullName,
    GitHubOwner Owner);

public record GitHubOwner(string Login);
