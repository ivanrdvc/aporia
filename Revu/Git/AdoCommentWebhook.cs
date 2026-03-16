using System.Text.Json.Serialization;

namespace Revu.Git;

/// <summary>
/// ADO service hook payload for <c>ms.vss-code.git-pullrequest-comment-event</c>.
/// The <c>resource</c> contains nested <c>comment</c> and <c>pullRequest</c> objects.
/// </summary>
public record AdoCommentWebhook(string EventType, AdoCommentResource Resource, AdoResourceContainers? ResourceContainers)
{
    private const string ChatMarker = "<!-- revu:chat -->";

    public ChatRequest? ToChatRequest()
    {
        if (EventType is not "ms.vss-code.git-pullrequest-comment-event")
            return null;

        var comment = Resource.Comment;
        if (comment is null or { IsDeleted: true } or { Content: null or "" })
            return null;

        // Only new comments, not edits
        if (comment.PublishedDate != comment.LastContentUpdatedDate)
            return null;

        // Self-reply loop prevention
        if (comment.Content.StartsWith(ChatMarker))
            return null;

        var pr = Resource.PullRequest;
        if (pr?.Repository is null)
            return null;

        var project = pr.Repository.Project?.Name
                      ?? ResourceContainers?.Project?.Id;
        if (project is null)
            return null;

        return new ChatRequest(
            Review: new ReviewRequest(
                Provider: GitProvider.Ado,
                Project: project,
                RepositoryId: pr.Repository.Id,
                RepositoryName: pr.Repository.Name ?? "",
                PullRequestId: pr.PullRequestId,
                SourceBranch: pr.SourceRefName ?? "",
                TargetBranch: pr.TargetRefName ?? ""),
            CommentId: comment.Id,
            UserMessage: comment.Content);
    }
}

/// <summary>
/// The resource object containing the comment and its associated pull request.
/// </summary>
public record AdoCommentResource
{
    public AdoComment? Comment { get; init; }
    public AdoCommentPullRequest? PullRequest { get; init; }
}

/// <summary>
/// An individual PR comment. Uses init properties because <c>_links</c> requires
/// a custom <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
public record AdoComment
{
    public int Id { get; init; }
    public int ParentCommentId { get; init; }
    public string? Content { get; init; }
    public DateTimeOffset PublishedDate { get; init; }
    public DateTimeOffset LastContentUpdatedDate { get; init; }
    public bool IsDeleted { get; init; }

    [JsonPropertyName("_links")]
    public AdoCommentLinks? Links { get; init; }
}

/// <summary>
/// Slimmed-down pull request included in comment webhook payloads.
/// </summary>
public record AdoCommentPullRequest
{
    public int PullRequestId { get; init; }
    public string? SourceRefName { get; init; }
    public string? TargetRefName { get; init; }
    public AdoCommentRepository? Repository { get; init; }
}

/// <summary>
/// Repository with project context, as nested in comment webhook payloads.
/// </summary>
public record AdoCommentRepository
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public AdoCommentProject? Project { get; init; }
}

public record AdoCommentProject(string? Id, string? Name);
public record AdoCommentLinks(AdoLinkRef? Self);
public record AdoLinkRef(string? Href);
public record AdoResourceContainers(AdoResourceContainer? Project);
public record AdoResourceContainer(string? Id);
