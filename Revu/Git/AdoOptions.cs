using System.ComponentModel.DataAnnotations;

namespace Revu.Git;

public class AdoOptions
{
    public const string SectionName = "AzureDevOps";

    [Required, MinLength(1)]
    public Dictionary<string, AdoOrgConfig> Organizations { get; init; } = [];
}

public class AdoOrgConfig
{
    [Required] public string Organization { get; init; } = string.Empty;
    [Required] public string PersonalAccessToken { get; init; } = string.Empty;
}
