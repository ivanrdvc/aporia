using Aporia.Git;

namespace Aporia.Tests.Unit.Git;

public class GitHubCommentWebhookTests
{
    [Fact]
    public void ToChatRequest_ReviewCommentReply_ReturnsChatRequest()
    {
        var webhook = CreateReviewCommentWebhook("I disagree with this", inReplyToId: 100);

        var result = webhook.ToChatRequest("pull_request_review_comment")!;

        Assert.NotNull(result);
        Assert.Equal(GitProvider.GitHub, result.Review.Provider);
        Assert.Equal("octocat", result.Review.Project);
        Assert.Equal("octocat/hello-world", result.Review.RepositoryId);
        Assert.Equal("hello-world", result.Review.RepositoryName);
        Assert.Equal(42, result.Review.PullRequestId);
        Assert.Equal(100, result.ThreadId);
        Assert.Equal(200, result.CommentId);
        Assert.Equal("I disagree with this", result.UserMessage);
        Assert.Equal(ChatCommentKind.ReviewComment, result.CommentKind);
    }

    [Fact]
    public void ToChatRequest_ReviewCommentRoot_ThreadIdIsOwnId()
    {
        var webhook = CreateReviewCommentWebhook("@aporia explain", inReplyToId: null);

        var result = webhook.ToChatRequest("pull_request_review_comment")!;

        Assert.NotNull(result);
        Assert.Equal(200, result.ThreadId);
        Assert.Equal(200, result.CommentId);
    }

    [Fact]
    public void ToChatRequest_IssueCommentWithMention_ReturnsChatRequest()
    {
        var webhook = CreateIssueCommentWebhook("@aporia what do you think?");

        var result = webhook.ToChatRequest("issue_comment")!;

        Assert.NotNull(result);
        Assert.Equal(ChatCommentKind.IssueComment, result.CommentKind);
        Assert.Equal(300, result.ThreadId);
        Assert.Equal(300, result.CommentId);
        Assert.Equal("@aporia what do you think?", result.UserMessage);
    }

    [Fact]
    public void ToChatRequest_IssueCommentWithoutMention_ReturnsNull()
    {
        var webhook = CreateIssueCommentWebhook("Just a regular comment");

        Assert.Null(webhook.ToChatRequest("issue_comment"));
    }

    [Fact]
    public void ToChatRequest_IssueCommentOnNonPr_ReturnsNull()
    {
        var webhook = new GitHubCommentWebhook(
            "created",
            new GitHubCommentPayload(300, "@aporia hello"),
            CreateRepo(),
            Issue: new GitHubIssueRef(10, PullRequest: null));

        Assert.Null(webhook.ToChatRequest("issue_comment"));
    }

    [Fact]
    public void ToChatRequest_AporiaMarkerComment_ReturnsNull()
    {
        var webhook = CreateReviewCommentWebhook("<!-- aporia:chat -->\nSome AI reply");

        Assert.Null(webhook.ToChatRequest("pull_request_review_comment"));
    }

    [Fact]
    public void ToChatRequest_ReviewMarker_ReturnsNull()
    {
        var webhook = CreateReviewCommentWebhook("<!-- aporia:fp:abc123 -->\n**Warning**: issue here");

        Assert.Null(webhook.ToChatRequest("pull_request_review_comment"));
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("deleted")]
    public void ToChatRequest_NonCreatedAction_ReturnsNull(string action)
    {
        var webhook = CreateReviewCommentWebhook("@aporia hello", action: action);

        Assert.Null(webhook.ToChatRequest("pull_request_review_comment"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToChatRequest_EmptyBody_ReturnsNull(string? body)
    {
        var webhook = CreateReviewCommentWebhook(body);

        Assert.Null(webhook.ToChatRequest("pull_request_review_comment"));
    }

    [Fact]
    public void ToChatRequest_UnknownEventType_ReturnsNull()
    {
        var webhook = CreateReviewCommentWebhook("hello");

        Assert.Null(webhook.ToChatRequest("push"));
    }

    [Fact]
    public void ToChatRequest_InstallationId_Preserved()
    {
        var webhook = CreateReviewCommentWebhook("hello", installationId: 12345);

        var result = webhook.ToChatRequest("pull_request_review_comment")!;

        Assert.Equal(12345L, result.Review.InstallationId);
    }

    static GitHubCommentWebhook CreateReviewCommentWebhook(
        string? body,
        long? inReplyToId = null,
        string action = "created",
        long? installationId = null) => new(
        action,
        new GitHubCommentPayload(200, body, inReplyToId),
        CreateRepo(),
        Installation: installationId is { } id ? new GitHubInstallation(id) : null,
        PullRequest: new GitHubPullRequest(42, false, new GitHubRef("feature", "abc"), new GitHubRef("main", "def")));

    static GitHubCommentWebhook CreateIssueCommentWebhook(string? body) => new(
        "created",
        new GitHubCommentPayload(300, body),
        CreateRepo(),
        Issue: new GitHubIssueRef(42, new GitHubIssuePrRef("https://api.github.com/repos/octocat/hello-world/pulls/42")));

    static GitHubRepository CreateRepo() => new("hello-world", "octocat/hello-world", new GitHubOwner("octocat"));
}
