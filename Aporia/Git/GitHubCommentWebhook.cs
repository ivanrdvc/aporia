using System.Text.Json.Serialization;

namespace Aporia.Git;

/// <summary>
/// Payload model for GitHub <c>pull_request_review_comment</c> and <c>issue_comment</c> events.
/// </summary>
public record GitHubCommentWebhook(
    string Action,
    GitHubCommentPayload Comment,
    GitHubRepository Repository,
    GitHubInstallation? Installation = null,
    [property: JsonPropertyName("pull_request")] GitHubPullRequest? PullRequest = null,
    GitHubIssueRef? Issue = null)
{
    public ChatRequest? ToChatRequest(string eventType)
    {
        if (Action is not "created")
            return null;

        if (Comment.Body is null or "")
            return null;

        if (Comment.Body.StartsWith(ChatRequest.MarkerPrefix))
            return null;

        if (Comment.User?.Type is "Bot")
            return null;

        var (prNumber, head, @base, kind) = eventType switch
        {
            "pull_request_review_comment" when PullRequest is not null =>
                (PullRequest.Number, PullRequest.Head.Ref, PullRequest.Base.Ref, ChatCommentKind.ReviewComment),
            "issue_comment" when Issue?.PullRequest is not null
                && Comment.Body.Contains("@aporia", StringComparison.OrdinalIgnoreCase) =>
                (Issue.Number, "", "", ChatCommentKind.IssueComment),
            _ => default
        };

        if (prNumber == 0)
            return null;

        return new ChatRequest(
            Review: new ReviewRequest(
                Provider: GitProvider.GitHub,
                Project: Repository.Owner.Login,
                RepositoryId: Repository.FullName.Replace("/", "__"),
                RepositoryName: Repository.Name,
                PullRequestId: prNumber,
                SourceBranch: head,
                TargetBranch: @base,
                InstallationId: Installation?.Id),
            ThreadId: Comment.InReplyToId ?? Comment.Id,
            CommentId: Comment.Id,
            UserMessage: Comment.Body,
            CommentKind: kind);
    }
}

public record GitHubCommentPayload(
    long Id,
    string? Body,
    [property: JsonPropertyName("in_reply_to_id")] long? InReplyToId = null,
    GitHubCommentUser? User = null);

public record GitHubCommentUser(string? Type);

public record GitHubIssueRef(
    int Number,
    [property: JsonPropertyName("pull_request")] GitHubIssuePrRef? PullRequest = null);

public record GitHubIssuePrRef(string? Url);
