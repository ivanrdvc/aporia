using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Revu.Infra.Cosmos;

/// <summary>
/// Persists a review event for every pipeline run. Captures status, findings count,
/// and links to the AI session for debugging.
/// </summary>
public interface IReviewStore
{
    Task SaveAsync(string repositoryId, int pullRequestId, string? cursor,
                   ReviewStatus status, int findingsCount,
                   string? conversationId);
}

public class ReviewStore(CosmosDb db) : IReviewStore
{
    private readonly Container _container = db.Container(CosmosOptions.ReviewsContainer);

    public async Task SaveAsync(string repositoryId, int pullRequestId, string? cursor,
                                ReviewStatus status, int findingsCount,
                                string? conversationId)
    {
        var review = new ReviewEvent
        {
            Id = ToId(repositoryId, pullRequestId, cursor),
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            Cursor = cursor,
            Status = status,
            FindingsCount = findingsCount,
            ConversationId = conversationId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _container.UpsertItemAsync(review, new PartitionKey(repositoryId));
    }

    private static string ToId(string repositoryId, int pullRequestId, string? cursor)
        => $"{repositoryId}-pr-{pullRequestId}-{cursor ?? "0"}";
}

public class ReviewEvent
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("repositoryId")]
    public string RepositoryId { get; init; } = null!;

    [JsonProperty("pullRequestId")]
    public int PullRequestId { get; init; }

    [JsonProperty("cursor")]
    public string? Cursor { get; init; }

    [JsonProperty("status")]
    [JsonConverter(typeof(StringEnumConverter))]
    public ReviewStatus Status { get; init; }

    [JsonProperty("findingsCount")]
    public int FindingsCount { get; init; }

    [JsonProperty("conversationId")]
    public string? ConversationId { get; init; }

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public enum ReviewStatus { Completed, Failed, Skipped }
