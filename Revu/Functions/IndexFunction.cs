using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Revu.CodeGraph;

namespace Revu.Functions;

public class IndexFunction(CodeGraphIndexer indexer, ILogger<IndexFunction> logger)
{
    [Function("IndexProcessor")]
    public async Task Run([QueueTrigger("index-queue")] IndexRequest req)
    {
        logger.LogInformation("Indexing {RepoId} branch {Branch}", req.RepositoryId, req.Branch);
        await indexer.IndexAsync(req, CancellationToken.None);
        logger.LogInformation("Indexing complete for {RepoId}", req.RepositoryId);
    }
}
