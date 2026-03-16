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
                   string? conversationId, ReviewSnapshot? snapshot = null);

    Task<ReviewSnapshot?> GetLatestSnapshotAsync(string repositoryId, int pullRequestId);
}

public class ReviewStore(CosmosDb db) : IReviewStore
{
    private readonly Container _container = db.Container(CosmosOptions.ReviewsContainer);

    public async Task SaveAsync(string repositoryId, int pullRequestId, string? cursor,
                                ReviewStatus status, int findingsCount,
                                string? conversationId, ReviewSnapshot? snapshot = null)
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
            SnapshotJson = snapshot is not null ? System.Text.Json.JsonSerializer.Serialize(snapshot) : null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _container.UpsertItemAsync(review, new PartitionKey(repositoryId));
    }

    public async Task<ReviewSnapshot?> GetLatestSnapshotAsync(string repositoryId, int pullRequestId)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 c.snapshotJson FROM c WHERE c.pullRequestId = @prId AND c.status = 'Completed' AND c.snapshotJson != null ORDER BY c.createdAt DESC")
            .WithParameter("@prId", pullRequestId);

        using var iterator = _container.GetItemQueryIterator<ReviewEvent>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(repositoryId)
        });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var evt = response.FirstOrDefault();
            if (evt?.SnapshotJson is not null)
                return System.Text.Json.JsonSerializer.Deserialize<ReviewSnapshot>(evt.SnapshotJson);
        }

        return null;
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

    [JsonProperty("snapshotJson")]
    public string? SnapshotJson { get; init; }

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public enum ReviewStatus { Completed, Failed, Skipped }
