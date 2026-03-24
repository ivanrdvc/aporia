using System.Net;

using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using Aporia.Git;

namespace Aporia.Infra.Cosmos;

/// <summary>
/// Persists doc watch project registrations. Maps source repos to their docs
/// repo so the webhook can route changes to the doc watch pipeline.
/// </summary>
public interface IDocWatchStore
{
    Task<DocWatchProject?> GetAsync(string docsRepoId);
    Task<IReadOnlyList<DocWatchProject>> GetBySourceAsync(string sourceRepoId);
    Task SaveAsync(DocWatchProject project);
    Task DeleteAsync(string docsRepoId);
}

public class DocWatchStore(CosmosDb db) : IDocWatchStore
{
    private readonly Container _container = db.Container(CosmosOptions.DocWatchContainer);

    public async Task<DocWatchProject?> GetAsync(string docsRepoId)
    {
        try
        {
            return (await _container.ReadItemAsync<DocWatchProject>(
                docsRepoId, new PartitionKey(docsRepoId))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DocWatchProject>> GetBySourceAsync(string sourceRepoId)
    {
        var query = _container.GetItemLinqQueryable<DocWatchProject>()
            .Where(p => p.Enabled && p.SourceRepos.Contains(sourceRepoId))
            .ToFeedIterator();

        var results = new List<DocWatchProject>();
        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task SaveAsync(DocWatchProject project) =>
        await _container.UpsertItemAsync(project, new PartitionKey(project.Id));

    public async Task DeleteAsync(string docsRepoId)
    {
        try
        {
            await _container.DeleteItemAsync<DocWatchProject>(
                docsRepoId, new PartitionKey(docsRepoId));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    }
}

public class DocWatchProject
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("sourceRepos")]
    public List<string> SourceRepos { get; init; } = [];

    [JsonProperty("provider")]
    [JsonConverter(typeof(StringEnumConverter))]
    public GitProvider Provider { get; init; }

    [JsonProperty("organization")]
    public string Organization { get; init; } = null!;

    [JsonProperty("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}
