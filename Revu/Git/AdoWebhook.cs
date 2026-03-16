
namespace Revu.Git;

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

/// <summary>
/// Payload model for the ADO <c>ms.vss-code.git-pullrequest-comment-event</c> service hook.
/// </summary>
public record AdoCommentWebhook(string EventType, AdoCommentResource Resource)
{
    private const string ChatMarker = "<!-- revu:chat -->";

    public ChatRequest? ToChatRequest()
    {
        if (EventType is not "ms.vss-code.git-pullrequest-comment-event")
            return null;

        var comment = Resource.Comment;

        if (comment.IsDeleted || string.IsNullOrEmpty(comment.Content))
            return null;

        // Only new comments, not edits
        if (comment.PublishedDate != comment.LastContentUpdatedDate)
            return null;

        // Self-reply loop prevention
        if (comment.Content.StartsWith(ChatMarker))
            return null;

        var pr = Resource.PullRequest;
        var repo = pr.Repository;

        return new ChatRequest(
            Review: new ReviewRequest(
                Provider: GitProvider.Ado,
                Project: repo.Project.Name,
                RepositoryId: repo.Id,
                RepositoryName: repo.Name,
                PullRequestId: pr.PullRequestId,
                SourceBranch: pr.SourceRefName,
                TargetBranch: pr.TargetRefName),
            CommentId: comment.Id,
            UserMessage: comment.Content);
    }
}

public record AdoCommentResource(AdoComment Comment, AdoPullRequest PullRequest);
public record AdoComment(int Id, int ParentCommentId, AdoAuthor Author, string? Content,
    DateTimeOffset PublishedDate, DateTimeOffset LastContentUpdatedDate, bool IsDeleted);
public record AdoAuthor(string Id, string DisplayName, string UniqueName);
