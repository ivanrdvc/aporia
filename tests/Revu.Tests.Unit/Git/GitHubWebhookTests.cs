using Revu.Git;

namespace Revu.Tests.Unit.Git;

public class GitHubWebhookTests
{
    private static GitHubWebhook CreateWebhook(string action = "opened", bool draft = false) =>
        new(action,
            new GitHubPullRequest(42, draft,
                new GitHubRef("feature-branch", "abc123"),
                new GitHubRef("main", "def456")),
            new GitHubRepository("my-repo", "octocat/my-repo", new GitHubOwner("octocat")));

    [Theory]
    [InlineData("opened")]
    [InlineData("synchronize")]
    [InlineData("reopened")]
    public void ToRequest_SupportedAction_ReturnsRequest(string action)
    {
        var webhook = CreateWebhook(action);
        var result = webhook.ToRequest();

        Assert.NotNull(result);
        Assert.Equal(GitProvider.GitHub, result.Provider);
        Assert.Equal("octocat", result.Project);
        Assert.Equal("octocat/my-repo", result.RepositoryId);
        Assert.Equal("my-repo", result.RepositoryName);
        Assert.Equal(42, result.PullRequestId);
        Assert.Equal("feature-branch", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("edited")]
    [InlineData("labeled")]
    [InlineData("review_requested")]
    public void ToRequest_UnsupportedAction_ReturnsNull(string action)
    {
        Assert.Null(CreateWebhook(action).ToRequest());
    }

    [Fact]
    public void ToRequest_DraftPr_ReturnsNull()
    {
        Assert.Null(CreateWebhook(draft: true).ToRequest());
    }

    [Fact]
    public void ToRequest_DraftWithSynchronize_ReturnsNull()
    {
        Assert.Null(CreateWebhook("synchronize", draft: true).ToRequest());
    }
}
