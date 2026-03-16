using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Revu.CodeGraph;

namespace Revu.Functions;

public class IndexFunction(CodeGraphIndexer indexer, ILogger<IndexFunction> logger)
{
    [Function("IndexProcessor")]
    public async Task Run([QueueTrigger("%IndexQueue%")] IndexRequest req, CancellationToken ct)
    {
        logger.LogInformation("Indexing {RepoId} branch {Branch}", req.RepositoryId, req.Branch);
        await indexer.IndexAsync(req, ct);
        logger.LogInformation("Indexing complete for {RepoId}", req.RepositoryId);
    }
}
