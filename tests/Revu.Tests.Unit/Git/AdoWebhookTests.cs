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
    [Fact]
    public void ToChatRequest_ValidComment_ReturnsChatRequest()
    {
        var webhook = CreateCommentWebhook("@revu explain this");

        var result = webhook.ToChatRequest()!;

        Assert.NotNull(result);
        Assert.Equal(GitProvider.Ado, result.Review.Provider);
        Assert.Equal("my-project", result.Review.Project);
        Assert.Equal("repo-abc", result.Review.RepositoryId);
        Assert.Equal("my-repo", result.Review.RepositoryName);
        Assert.Equal(42, result.Review.PullRequestId);
        Assert.Equal(10, result.ThreadId);
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
    public void ToChatRequest_ChatMarker_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("<!-- revu:chat -->\nSome AI reply");

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_ReviewMarker_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("<!-- revu:review -->\nSome finding text");

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_TopLevelWithoutMention_ReturnsNull()
    {
        var webhook = CreateCommentWebhook("Just a regular comment", parentCommentId: 0);

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_TopLevelWithMention_ReturnsChatRequest()
    {
        var webhook = CreateCommentWebhook("@revu what do you think?", parentCommentId: 0);

        Assert.NotNull(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_ReplyWithoutMention_ReturnsChatRequest()
    {
        var webhook = CreateCommentWebhook("I disagree with this finding", parentCommentId: 1);

        Assert.NotNull(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_NoSelfLink_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var webhook = new AdoCommentWebhook(
            "ms.vss-code.git-pullrequest-comment-event",
            new AdoCommentResource
            {
                Comment = new AdoComment
                {
                    Id = 3,
                    Content = "@revu hello",
                    PublishedDate = now,
                    LastContentUpdatedDate = now,
                },
                PullRequest = new AdoCommentPullRequest
                {
                    PullRequestId = 42,
                    Repository = new AdoCommentRepository
                    {
                        Id = "repo-abc",
                        Name = "my-repo",
                        Project = new AdoCommentProject("proj-id", "my-project")
                    }
                }
            },
            new AdoResourceContainers(new AdoResourceContainer("proj-id")));

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_NoPullRequest_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var webhook = new AdoCommentWebhook(
            "ms.vss-code.git-pullrequest-comment-event",
            new AdoCommentResource
            {
                Comment = new AdoComment
                {
                    Id = 3,
                    Content = "@revu hello",
                    PublishedDate = now,
                    LastContentUpdatedDate = now,
                },
                PullRequest = null
            },
            new AdoResourceContainers(new AdoResourceContainer("proj-id")));

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_NoProject_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var webhook = new AdoCommentWebhook(
            "ms.vss-code.git-pullrequest-comment-event",
            new AdoCommentResource
            {
                Comment = new AdoComment
                {
                    Id = 3,
                    Content = "@revu hello",
                    PublishedDate = now,
                    LastContentUpdatedDate = now,
                },
                PullRequest = new AdoCommentPullRequest
                {
                    PullRequestId = 42,
                    Repository = new AdoCommentRepository
                    {
                        Id = "repo-abc",
                        Name = "my-repo",
                        Project = null
                    }
                }
            },
            ResourceContainers: null);

        Assert.Null(webhook.ToChatRequest());
    }

    [Fact]
    public void ToChatRequest_FallsBackToResourceContainersProject()
    {
        var now = DateTimeOffset.UtcNow;
        var webhook = new AdoCommentWebhook(
            "ms.vss-code.git-pullrequest-comment-event",
            new AdoCommentResource
            {
                Comment = new AdoComment
                {
                    Id = 3,
                    Content = "@revu hello",
                    PublishedDate = now,
                    LastContentUpdatedDate = now,
                    Links = new AdoCommentLinks(new AdoLinkRef(
                        "https://dev.azure.com/org/_apis/git/repositories/repo-abc/pullRequests/42/threads/10/comments/3"))
                },
                PullRequest = new AdoCommentPullRequest
                {
                    PullRequestId = 42,
                    Repository = new AdoCommentRepository
                    {
                        Id = "repo-abc",
                        Name = "my-repo",
                        Project = null
                    }
                }
            },
            new AdoResourceContainers(new AdoResourceContainer("fallback-proj-id")));

        var result = webhook.ToChatRequest()!;

        Assert.NotNull(result);
        Assert.Equal("fallback-proj-id", result.Review.Project);
    }

    [Fact]
    public void ToChatRequest_JsonDeserialization_MatchesRealPayload()
    {
        var json = """
        {
            "eventType": "ms.vss-code.git-pullrequest-comment-event",
            "resource": {
                "comment": {
                    "id": 1,
                    "parentCommentId": 0,
                    "author": {
                        "displayName": "Ivan Radovic",
                        "id": "91aff1c0-fc5e-68ed-982a-74496032443b",
                        "uniqueName": "ivan@example.com"
                    },
                    "content": "@revu feedback this is a test!",
                    "publishedDate": "2025-07-22T23:05:57.18Z",
                    "lastUpdatedDate": "2025-07-22T23:05:57.18Z",
                    "lastContentUpdatedDate": "2025-07-22T23:05:57.18Z",
                    "commentType": "text",
                    "isDeleted": false,
                    "_links": {
                        "self": {
                            "href": "https://dev.azure.com/myorg/_apis/git/repositories/298f9c48-cb77-4207-8a11-e6f4377d09cf/pullRequests/21/threads/836/comments/1"
                        }
                    }
                },
                "pullRequest": {
                    "repository": {
                        "id": "298f9c48-cb77-4207-8a11-e6f4377d09cf",
                        "name": "test-project-one",
                        "project": {
                            "id": "784a39f1-7620-4a3c-9cd9-7b558a722201",
                            "name": "Pilots Dev"
                        }
                    },
                    "pullRequestId": 21,
                    "status": "active",
                    "sourceRefName": "refs/heads/c-1",
                    "targetRefName": "refs/heads/master",
                    "isDraft": false
                }
            },
            "resourceContainers": {
                "collection": { "id": "6d522a72-a29e-4201-a0db-be6744b865c2" },
                "account": { "id": "5d624764-a901-45ac-b7f6-3760c1cfe5f3" },
                "project": { "id": "784a39f1-7620-4a3c-9cd9-7b558a722201" }
            }
        }
        """;

        var webhook = System.Text.Json.JsonSerializer.Deserialize<AdoCommentWebhook>(
            json, System.Text.Json.JsonSerializerOptions.Web)!;
        var result = webhook.ToChatRequest()!;

        Assert.NotNull(result);
        Assert.Equal("298f9c48-cb77-4207-8a11-e6f4377d09cf", result.Review.RepositoryId);
        Assert.Equal("test-project-one", result.Review.RepositoryName);
        Assert.Equal(21, result.Review.PullRequestId);
        Assert.Equal("Pilots Dev", result.Review.Project);
        Assert.Equal("refs/heads/c-1", result.Review.SourceBranch);
        Assert.Equal("refs/heads/master", result.Review.TargetBranch);
        Assert.Equal(836, result.ThreadId);
        Assert.Equal(1, result.CommentId);
        Assert.Equal("@revu feedback this is a test!", result.UserMessage);
    }

    static AdoCommentWebhook CreateCommentWebhook(
        string? content,
        string eventType = "ms.vss-code.git-pullrequest-comment-event",
        bool isDeleted = false,
        DateTimeOffset? publishedDate = null,
        DateTimeOffset? lastContentUpdatedDate = null,
        int parentCommentId = 0)
    {
        var now = publishedDate ?? DateTimeOffset.UtcNow;
        return new AdoCommentWebhook(
            eventType,
            new AdoCommentResource
            {
                Comment = new AdoComment
                {
                    Id = 3,
                    ParentCommentId = parentCommentId,
                    Content = content,
                    PublishedDate = now,
                    LastContentUpdatedDate = lastContentUpdatedDate ?? now,
                    IsDeleted = isDeleted,
                    Links = new AdoCommentLinks(new AdoLinkRef(
                        "https://dev.azure.com/org/_apis/git/repositories/repo-abc/pullRequests/42/threads/10/comments/3"))
                },
                PullRequest = new AdoCommentPullRequest
                {
                    PullRequestId = 42,
                    SourceRefName = "refs/heads/feature",
                    TargetRefName = "refs/heads/main",
                    Repository = new AdoCommentRepository
                    {
                        Id = "repo-abc",
                        Name = "my-repo",
                        Project = new AdoCommentProject("proj-id", "my-project")
                    }
                }
            },
            new AdoResourceContainers(new AdoResourceContainer("proj-id")));
    }
}
