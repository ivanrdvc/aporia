using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Revu.CodeGraph;
using Revu.Infra.Cosmos;

namespace Revu.Infra;

public static class ServiceCollectionExtensions
{
    public static void AddCosmos(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CosmosOptions>().BindConfiguration(CosmosOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        var cosmos = configuration.GetSection(CosmosOptions.SectionName).Get<CosmosOptions>()
                    ?? throw new InvalidOperationException($"{CosmosOptions.SectionName} configuration section is required.");
        var connectionString = cosmos.ConnectionString;

        var cosmosClient = new CosmosClient(connectionString);
        services.AddSingleton(cosmosClient);

        // TODO: move container init to IHostedService for proper async startup
        var db = cosmosClient.CreateDatabaseIfNotExistsAsync(cosmos.Database).GetAwaiter().GetResult();
        db.Database.CreateContainerIfNotExistsAsync(CosmosOptions.SessionsContainer, "/conversationId").GetAwaiter().GetResult();
        db.Database.CreateContainerIfNotExistsAsync(new ContainerProperties(CosmosOptions.PrStateContainer, "/repositoryId")
            { DefaultTimeToLive = (int)TimeSpan.FromDays(90).TotalSeconds }).GetAwaiter().GetResult();
        db.Database.CreateContainerIfNotExistsAsync(CosmosOptions.RepositoriesContainer, "/id").GetAwaiter().GetResult();
        db.Database.CreateContainerIfNotExistsAsync(CosmosOptions.ReviewsContainer, "/repositoryId").GetAwaiter().GetResult();

        db.Database.CreateContainerIfNotExistsAsync(CosmosOptions.CodeGraphContainer, "/repoId").GetAwaiter().GetResult();

        services.AddSingleton<CosmosDb>();
        services.AddSingleton<IRepoStore, RepoStore>();
        services.AddSingleton<IPrStateStore, PrStateStore>();
        services.AddSingleton<IReviewStore, ReviewStore>();
        services.AddCodeGraph();

        services.AddSingleton<ChatHistoryProvider>(sp =>
            new CosmosChatHistoryProvider(
                sp.GetRequiredService<CosmosClient>(),
                cosmos.Database,
                CosmosOptions.SessionsContainer,
                session => new CosmosChatHistoryProvider.State(
                    session?.StateBag.TryGetValue<string>(SessionKeys.ConversationId, out var id) == true && id is not null
                        ? id
                        : Guid.NewGuid().ToString("N")),
                ownsClient: false)
            { MessageTtlSeconds = (int)TimeSpan.FromDays(180).TotalSeconds });
    }

    private static void AddCodeGraph(this IServiceCollection services)
    {
        services.AddSingleton<ICodeGraphStore, CodeGraphStore>();
        services.AddSingleton<ILanguageParser, CSharpParser>();
        services.AddSingleton<ILanguageParser, TypeScriptParser>();
        services.AddSingleton<CodeGraphIndexer>();
    }
}
