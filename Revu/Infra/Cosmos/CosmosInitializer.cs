using System;

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Revu.Infra.Cosmos;

/// <summary>
/// Ensures Cosmos database and containers exist using async startup to avoid blocking the host.
/// </summary>
public class CosmosInitializer(
    CosmosClient client,
    IOptions<CosmosOptions> options,
    ILogger<CosmosInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cosmos = options.Value;

        var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(
            cosmos.Database,
            cancellationToken: cancellationToken);

        var database = databaseResponse.Database;

        await database.CreateContainerIfNotExistsAsync(
            CosmosOptions.SessionsContainer,
            "/conversationId",
            cancellationToken: cancellationToken);

        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(CosmosOptions.PrStateContainer, "/repositoryId")
            {
                DefaultTimeToLive = (int)TimeSpan.FromDays(90).TotalSeconds
            },
            cancellationToken: cancellationToken);

        await database.CreateContainerIfNotExistsAsync(
            CosmosOptions.RepositoriesContainer,
            "/id",
            cancellationToken: cancellationToken);

        await database.CreateContainerIfNotExistsAsync(
            CosmosOptions.ReviewsContainer,
            "/repositoryId",
            cancellationToken: cancellationToken);

        await database.CreateContainerIfNotExistsAsync(
            CosmosOptions.CodeGraphContainer,
            "/repoId",
            cancellationToken: cancellationToken);

        logger.LogInformation("Cosmos containers ensured for database {Database}", cosmos.Database);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
