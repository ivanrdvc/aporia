using System.Net;

using Microsoft.Azure.Cosmos;

using Revu.CodeGraph;

namespace Revu.Infra.Cosmos;

public interface ICodeGraphStore
{
    Task UpsertFileAsync(FileIndex file);
    Task DeleteOrphansAsync(string repoId, HashSet<string> indexedPaths);
    Task<List<FileIndex>> GetAllAsync(string repoId);
}

public class CodeGraphStore(CosmosDb db) : ICodeGraphStore
{
    private readonly Container _container = db.Container(CosmosOptions.CodeGraphContainer);

    public async Task UpsertFileAsync(FileIndex file) =>
        await _container.UpsertItemAsync(file, new PartitionKey(file.RepoId));

    public async Task DeleteOrphansAsync(string repoId, HashSet<string> indexedPaths)
    {
        var query = _container.GetItemQueryIterator<dynamic>(
            new QueryDefinition("SELECT c.id FROM c WHERE c.repoId = @repoId")
                .WithParameter("@repoId", repoId),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(repoId) });

        var orphanIds = new List<string>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            foreach (var doc in page)
            {
                string id = doc.id;
                if (!indexedPaths.Contains(id))
                    orphanIds.Add(id);
            }
        }

        var pk = new PartitionKey(repoId);
        await Parallel.ForEachAsync(orphanIds, new ParallelOptions { MaxDegreeOfParallelism = 10 },
            async (id, _) => await _container.DeleteItemAsync<FileIndex>(id, pk));
    }

    public async Task<List<FileIndex>> GetAllAsync(string repoId)
    {
        var results = new List<FileIndex>();
        var query = _container.GetItemQueryIterator<FileIndex>(
            new QueryDefinition("SELECT * FROM c WHERE c.repoId = @repoId")
                .WithParameter("@repoId", repoId),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(repoId) });

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        return results;
    }
}
