using System.Net;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using Aporia.Git;

namespace Aporia.Infra.Cosmos;

/// <summary>
/// Persists repository registration and metadata. Gates the webhook — only
/// registered and enabled repos trigger reviews.
/// </summary>
public interface IRepoStore
{
    Task<Repository?> GetAsync(string repositoryId);
    Task SaveAsync(Repository repo);
}

public class RepoStore(CosmosDb db) : IRepoStore
{
    private readonly Container _container = db.Container(CosmosOptions.RepositoriesContainer);

    public async Task<Repository?> GetAsync(string repositoryId)
    {
        try
        {
            return (await _container.ReadItemAsync<Repository>(
                repositoryId, new PartitionKey(repositoryId))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task SaveAsync(Repository repo) =>
        await _container.UpsertItemAsync(repo, new PartitionKey(repo.Id));

}

public class Repository
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("provider")]
    [JsonConverter(typeof(StringEnumConverter))]
    public GitProvider Provider { get; init; }

    [JsonProperty("enabled")]
    public bool Enabled { get; init; }

    [JsonProperty("name")]
    public string? Name { get; init; }

    [JsonProperty("url")]
    public string? Url { get; init; }

    [JsonProperty("organization")]
    public string? Organization { get; init; }

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}
