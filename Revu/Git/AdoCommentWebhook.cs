using System.Text.Json.Serialization;

namespace Revu.Git;

/// <summary>
/// ADO service hook payload for <c>ms.vss-code.git-pullrequest-comment-event</c>.
/// The <c>resource</c> contains nested <c>comment</c> and <c>pullRequest</c> objects.
/// </summary>
public record AdoCommentWebhook(string EventType, AdoCommentResource Resource, AdoResourceContainers? ResourceContainers)
{
    private static string ChatMarker => ChatRequest.ChatMarker;
    private static string ReviewMarker => ChatRequest.ReviewMarker;

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

        // Ignore Revu's own comments (review findings, summary, and chat replies)
        if (comment.Content.StartsWith(ChatMarker) || comment.Content.StartsWith(ReviewMarker))
            return null;

        // Top-level comments (new threads) need an explicit @revu mention.
        // Replies (ParentCommentId > 0) are let through — they might be on a Revu thread,
        // which we can only verify downstream via the ADO API.
        if (comment.ParentCommentId == 0
            && !comment.Content.Contains("@revu", StringComparison.OrdinalIgnoreCase))
            return null;

        var pr = Resource.PullRequest;
        if (pr?.Repository is null)
            return null;

        if (!TryParseThreadId(comment.Links?.Self?.Href, out var threadId))
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
            ThreadId: threadId,
            CommentId: comment.Id,
            UserMessage: comment.Content);
    }
    /// <summary>
    /// Extracts the thread ID from the comment's self link URL.
    /// Format: .../threads/{threadId}/comments/{commentId}
    /// </summary>
    internal static bool TryParseThreadId(string? url, out int threadId)
    {
        threadId = 0;
        if (url is null) return false;

        const string seg = "/threads/";
        var i = url.IndexOf(seg, StringComparison.Ordinal);
        if (i < 0) return false;

        var start = i + seg.Length;
        var end = url.IndexOf('/', start);
        var str = end >= 0 ? url[start..end] : url[start..];
        return int.TryParse(str, out threadId);
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
