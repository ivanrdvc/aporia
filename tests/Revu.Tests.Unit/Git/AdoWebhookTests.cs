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

public class AdoCommentWebhookTests
{
    private const string SelfLink =
        "https://dev.azure.com/myorg/proj/_apis/git/repositories/repo-abc/pullRequests/42/threads/10/comments/3";

    [Fact]
    public void ToChatRequest_ValidComment_ReturnsChatRequest()
    {
        var webhook = CreateCommentWebhook("@revu explain this");

        var result = webhook.ToChatRequest()!;

        Assert.NotNull(result);
        Assert.Equal(GitProvider.Ado, result.Review.Provider);
        Assert.Equal("proj-id", result.Review.Project);
        Assert.Equal("repo-abc", result.Review.RepositoryId);
        Assert.Equal(42, result.Review.PullRequestId);
        Assert.Equal(3, result.CommentId);
        Assert.Equal("@revu explain this", result.UserMessage);
    }

    [Fact]
    public void ToChatRequest_WrongEventType_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("@revu hello", eventType: "git.pullrequest.updated");

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_DeletedComment_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("@revu hello", isDeleted: true);

        Assert.Null(webhook.ToChatRequest());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToChatRequest_EmptyContent_ReturnsNull(string? content)
    {
        var webhook = CreateCommentWebhook(content);

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_EditedComment_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var webhook = CreateCommentWebhook("@revu hello",
            publishedDate: now,
            lastContentUpdatedDate: now.AddMinutes(1));

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_SelfReply_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("<!-- revu:chat -->\nSome AI reply");

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_NoSelfLink_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("@revu hello", selfLink: null);

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_NoProjectContainer_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("@revu hello", projectId: null);

        Assert.Null(webhook.ToChatRequest());
    }

    [Theory]
    [InlineData(SelfLink, "repo-abc", 42)]
    [InlineData("https://dev.azure.com/org/proj/_apis/git/repositories/guid-123/pullRequests/7/threads/1/comments/1", "guid-123", 7)]
    [InlineData("https://dev.azure.com/org/proj/_apis/git/repositories/guid-123/pullRequests/99", "guid-123", 99)]
    public void TryParseSelfLink_ValidUrl_ExtractsIds(string url, string expectedRepoId, int expectedPrId)
    {
        var result = AdoCommentWebhook.TryParseSelfLink(url, out var repoId, out var prId);

        Assert.True(result);
        Assert.Equal(expectedRepoId, repoId);
        Assert.Equal(expectedPrId, prId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://dev.azure.com/org/proj/_apis/git/items")]
    [InlineData("https://dev.azure.com/org/proj/_apis/git/repositories/repo-abc")]
    public void TryParseSelfLink_InvalidUrl_ReturnsFalse(string? url)
    {
        var result = AdoCommentWebhook.TryParseSelfLink(url, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void ToChatRequest_JsonDeserialization_Works()
    {
        var json = """
        {
            "eventType": "ms.vss-code.git-pullrequest-comment-event",
            "resource": {
                "id": 5,
                "content": "@revu test",
                "publishedDate": "2026-03-16T12:00:00Z",
                "lastContentUpdatedDate": "2026-03-16T12:00:00Z",
                "isDeleted": false,
                "_links": {
                    "self": {
                        "href": "https://dev.azure.com/org/proj/_apis/git/repositories/repo-1/pullRequests/10/threads/1/comments/5"
                    }
                }
            },
            "resourceContainers": {
                "project": { "id": "proj-guid" }
            }
        }
        """;

        var webhook = System.Text.Json.JsonSerializer.Deserialize<AdoCommentWebhook>(
            json, System.Text.Json.JsonSerializerOptions.Web)!;
        var result = webhook.ToChatRequest()!;

        Assert.NotNull(result);
        Assert.Equal("repo-1", result.Review.RepositoryId);
        Assert.Equal(10, result.Review.PullRequestId);
        Assert.Equal("proj-guid", result.Review.Project);
        Assert.Equal(5, result.CommentId);
        Assert.Equal("@revu test", result.UserMessage);
    }

    static AdoCommentWebhook CreateCommentWebhook(
        string? content,
        string eventType = "ms.vss-code.git-pullrequest-comment-event",
        bool isDeleted = false,
        string? selfLink = SelfLink,
        string? projectId = "proj-id",
        DateTimeOffset? publishedDate = null,
        DateTimeOffset? lastContentUpdatedDate = null)
    {
        var now = publishedDate ?? DateTimeOffset.UtcNow;
        return new AdoCommentWebhook(
            eventType,
            new AdoCommentResource
            {
                Id = 3,
                Content = content,
                PublishedDate = now,
                LastContentUpdatedDate = lastContentUpdatedDate ?? now,
                IsDeleted = isDeleted,
                Links = selfLink is not null
                    ? new AdoCommentLinks(new AdoLinkRef(selfLink))
                    : null
            },
            projectId is not null
                ? new AdoResourceContainers(new AdoResourceContainer(projectId))
                : null);
    }
}
