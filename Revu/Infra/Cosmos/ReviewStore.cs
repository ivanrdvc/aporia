using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Revu.Infra.Cosmos;

/// <summary>
/// Persists a review event for every pipeline run. Captures status, findings count,
/// duration, and links to the AI session for debugging.
/// </summary>
public interface IReviewStore
{
    Task SaveAsync(string repositoryId, int pullRequestId, int? iterationId,
                   ReviewStatus status, int findingsCount, long durationMs,
                   string? conversationId);
}

public class ReviewStore(CosmosDb db) : IReviewStore
{
    private readonly Container _container = db.Container(CosmosOptions.ReviewsContainer);

    public async Task SaveAsync(string repositoryId, int pullRequestId, int? iterationId,
                                ReviewStatus status, int findingsCount, long durationMs,
                                string? conversationId)
    {
        var review = new ReviewEvent
        {
            Id = ToId(repositoryId, pullRequestId, iterationId),
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            IterationId = iterationId,
            Status = status,
            FindingsCount = findingsCount,
            DurationMs = durationMs,
            ConversationId = conversationId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _container.UpsertItemAsync(review, new PartitionKey(repositoryId));
    }

    private static string ToId(string repositoryId, int pullRequestId, int? iterationId)
        => $"{repositoryId}-pr-{pullRequestId}-{iterationId ?? 0}";
}

public class ReviewEvent
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("repositoryId")]
    public string RepositoryId { get; init; } = null!;

    [JsonProperty("pullRequestId")]
    public int PullRequestId { get; init; }

    [JsonProperty("iterationId")]
    public int? IterationId { get; init; }

    [JsonProperty("status")]
    [JsonConverter(typeof(StringEnumConverter))]
    public ReviewStatus Status { get; init; }

    [JsonProperty("findingsCount")]
    public int FindingsCount { get; init; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; init; }

    [JsonProperty("conversationId")]
    public string? ConversationId { get; init; }

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public enum ReviewStatus { Completed, Failed, Skipped }
