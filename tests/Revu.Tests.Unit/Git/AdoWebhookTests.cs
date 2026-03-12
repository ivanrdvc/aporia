using Revu.Git;

namespace Revu.Tests.Unit.Git;

public class AdoWebhookTests
{
    [Theory]
    [InlineData("git.pullrequest.created")]
    [InlineData("git.pullrequest.updated")]
    public void ToRequest_ValidEvent_ReturnsRequest(string eventType)
    {
        var webhook = CreateWebhook(eventType);

        var result = webhook.ToRequest();

        Assert.NotNull(result);
    }

    [Fact]
    public void ToRequest_Created_MapsFieldsCorrectly()
    {
        var webhook = CreateWebhook("git.pullrequest.created");

        var result = webhook.ToRequest()!;

        Assert.Equal(GitProvider.Ado, result.Provider);
        Assert.Equal("my-project", result.Project);
        Assert.Equal("repo-123", result.RepositoryId);
        Assert.Equal("my-repo", result.RepositoryName);
        Assert.Equal(42, result.PullRequestId);
        Assert.Equal("refs/heads/feature/login", result.SourceBranch);
        Assert.Equal("refs/heads/main", result.TargetBranch);
    }

    [Theory]
    [InlineData("git.pullrequest.created")]
    [InlineData("git.pullrequest.updated")]
    public void ToRequest_DraftPr_ReturnsNull(string eventType)
    {
        var webhook = CreateWebhook(eventType, isDraft: true);

        var result = webhook.ToRequest();

        Assert.Null(result);
    }

    [Theory]
    [InlineData("git.pullrequest.merged")]
    [InlineData("git.pullrequest.abandoned")]
    [InlineData("git.push")]
    [InlineData("build.complete")]
    public void ToRequest_UnhandledEvent_ReturnsNull(string eventType)
    {
        var webhook = CreateWebhook(eventType);

        var result = webhook.ToRequest();

        Assert.Null(result);
    }

    static AdoWebhook CreateWebhook(string eventType, bool isDraft = false) => new(
        eventType,
        new AdoPullRequest(
            PullRequestId: 42,
            SourceRefName: "refs/heads/feature/login",
            TargetRefName: "refs/heads/main",
            IsDraft: isDraft,
            Repository: new AdoRepository(
                Id: "repo-123",
                Name: "my-repo",
                Project: new AdoProject(Name: "my-project"))));
}
