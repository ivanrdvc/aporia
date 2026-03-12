using System.ComponentModel.DataAnnotations;

namespace Revu.Infra.Cosmos;

public class CosmosOptions
{
    public const string SectionName = "Cosmos";

    public const string RepositoriesContainer = "repositories";
    public const string PrStateContainer = "pr-state";
    public const string ReviewsContainer = "reviews";
    public const string SessionsContainer = "sessions";

    [Required]
    public string ConnectionString { get; init; } = null!;

    public string Database { get; init; } = "revu";
}
