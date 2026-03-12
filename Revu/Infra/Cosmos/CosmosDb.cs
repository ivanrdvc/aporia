using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Revu.Infra.Cosmos;

/// <summary>
/// Shared Cosmos DB infrastructure — owns the client and database config.
/// Domain-specific stores depend on this to get their container.
/// </summary>
public class CosmosDb(CosmosClient client, IOptions<CosmosOptions> options)
{
    public Container Container(string name) => client.GetContainer(options.Value.Database, name);
}
