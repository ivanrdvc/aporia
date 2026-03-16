using System.Text.Json.Serialization;

namespace Revu.Git;

/// <summary>
/// ADO service hook payload for <c>ms.vss-code.git-pullrequest-comment-event</c>.
/// In this event type, <c>resource</c> IS the comment directly — PR and repo IDs
/// are extracted from the comment's <c>_links.self</c> URL.
/// </summary>
public record AdoCommentWebhook(string EventType, AdoCommentResource Resource, AdoResourceContainers? ResourceContainers)
{
    private const string ChatMarker = "<!-- revu:chat -->";

    public ChatRequest? ToChatRequest()
    {
        if (EventType is not "ms.vss-code.git-pullrequest-comment-event")
            return null;

        if (Resource is not { IsDeleted: false, Content: not (null or "") })
            return null;

        // Only new comments, not edits
        if (Resource.PublishedDate != Resource.LastContentUpdatedDate)
            return null;

        // Self-reply loop prevention
        if (Resource.Content.StartsWith(ChatMarker))
            return null;

        if (!TryParseSelfLink(Resource.Links?.Self?.Href, out var repoId, out var prId))
            return null;

        var projectId = ResourceContainers?.Project?.Id;
        if (projectId is null)
            return null;

        return new ChatRequest(
            Review: new ReviewRequest(
                Provider: GitProvider.Ado,
                Project: projectId,
                RepositoryId: repoId,
                RepositoryName: "",
                PullRequestId: prId,
                SourceBranch: "",
                TargetBranch: ""),
            CommentId: Resource.Id,
            UserMessage: Resource.Content);
    }

    /// <summary>
    /// Parses repository ID and PR ID from the self link URL.
    /// Format: .../repositories/{repoId}/pullRequests/{prId}/threads/{threadId}/comments/{commentId}
    /// </summary>
    internal static bool TryParseSelfLink(string? url, out string repoId, out int prId)
    {
        repoId = "";
        prId = 0;
        if (url is null) return false;

        const string repoSeg = "/repositories/";
        const string prSeg = "/pullRequests/";

        var ri = url.IndexOf(repoSeg, StringComparison.Ordinal);
        var pi = url.IndexOf(prSeg, StringComparison.Ordinal);
        if (ri < 0 || pi < 0) return false;

        var repoStart = ri + repoSeg.Length;
        var repoEnd = url.IndexOf('/', repoStart);
        if (repoEnd < 0) return false;
        repoId = url[repoStart..repoEnd];

        var prStart = pi + prSeg.Length;
        var prEnd = url.IndexOf('/', prStart);
        var prStr = prEnd >= 0 ? url[prStart..prEnd] : url[prStart..];
        return int.TryParse(prStr, out prId);
    }
}

/// <summary>
/// The comment resource. Uses init properties because <c>_links</c> requires
/// a custom <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
public record AdoCommentResource
{
    public int Id { get; init; }
    public string? Content { get; init; }
    public DateTimeOffset PublishedDate { get; init; }
    public DateTimeOffset LastContentUpdatedDate { get; init; }
    public bool IsDeleted { get; init; }

    [JsonPropertyName("_links")]
    public AdoCommentLinks? Links { get; init; }
}

public record AdoCommentLinks(AdoLinkRef? Self);
public record AdoLinkRef(string? Href);
public record AdoResourceContainers(AdoResourceContainer? Project);
public record AdoResourceContainer(string? Id);
