using System.ComponentModel.DataAnnotations;

namespace Revu.Git;

public class GitHubOptions
{
    public const string SectionName = "GitHub";

    [Required, MinLength(1)]
    public Dictionary<string, GitHubOrgConfig> Organizations { get; init; } = [];

    public string? WebhookSecret { get; init; }
}

public class GitHubOrgConfig
{
    [Required] public string Token { get; init; } = string.Empty;
}
