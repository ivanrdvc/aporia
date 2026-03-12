using System.Net;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;

namespace Revu.Infra.Cosmos;

/// <summary>
/// Tracks per-PR pipeline state (last reviewed iteration) so incremental reviews
/// only examine new changes. Documents have a 90-day TTL.
/// </summary>
public interface IPrStateStore
{
    Task<PrState?> GetAsync(string repositoryId, int pullRequestId);
    Task SaveAsync(string repositoryId, int pullRequestId, int iterationId);
}

public class PrStateStore(CosmosDb db) : IPrStateStore
{
    private readonly Container _container = db.Container(CosmosOptions.PrStateContainer);

    public async Task<PrState?> GetAsync(string repositoryId, int pullRequestId)
    {
        try
        {
            return (await _container.ReadItemAsync<PrState>(
                ToId(repositoryId, pullRequestId),
                new PartitionKey(repositoryId))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveAsync(string repositoryId, int pullRequestId, int iterationId)
    {
        var state = new PrState
        {
            Id = ToId(repositoryId, pullRequestId),
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            IterationId = iterationId
        };

        await _container.UpsertItemAsync(state, new PartitionKey(repositoryId));
    }

    private static string ToId(string repositoryId, int pullRequestId) => $"{repositoryId}-pr-{pullRequestId}";
}

public class PrState
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("repositoryId")]
    public string RepositoryId { get; init; } = null!;

    [JsonProperty("pullRequestId")]
    public int PullRequestId { get; init; }

    [JsonProperty("iterationId")]
    public int IterationId { get; init; }
}
