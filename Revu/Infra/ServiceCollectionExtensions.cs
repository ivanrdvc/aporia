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
        services.AddHostedService<CosmosInitializer>();

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
