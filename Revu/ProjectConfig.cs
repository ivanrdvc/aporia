using System.Text.Json;

using Microsoft.Extensions.FileSystemGlobbing;

using Revu.Infra;

namespace Revu;

public class ProjectConfig
{
    public ReviewConfig Review { get; init; } = new();
    public FileConfig Files { get; init; } = new();
    public List<string> Rules { get; init; } = [];
    public string? Context { get; init; }

    public static readonly ProjectConfig Default = new()
    {
        Review = new ReviewConfig { MaxComments = 5 },
        Files = new FileConfig
        {
            AllowedExtensions = [".cs", ".ts", ".js", ".jsx", ".tsx"],
            Ignore =
            [
                "**/bin/**", "**/obj/**", "**/*.generated.cs", "**/node_modules/**",
                "**/package-lock.json", "**/yarn.lock", "**/*.min.js", "**/vendor/**"
            ]
        }
    };

    public static ProjectConfig Parse(string? raw)
    {
        var o = raw.TryParseJson<ProjectConfig>(JsonOptions);
        if (o is null)
            return Default;

        var d = Default;

        return new ProjectConfig
        {
            Review = new ReviewConfig
            {
                Strategy = o.Review.Strategy ?? d.Review.Strategy,
                MaxComments = o.Review.MaxComments ?? d.Review.MaxComments,
                EnableWorkItems = o.Review.EnableWorkItems ?? d.Review.EnableWorkItems
            },
            Files = new FileConfig
            {
                AllowedExtensions = Merge(d.Files.AllowedExtensions, o.Files.AllowedExtensions),
                Ignore = Merge(d.Files.Ignore, o.Files.Ignore)
            },
            Rules = Merge(d.Rules, o.Rules),
            Context = o.Context ?? d.Context
        };
    }

    private static List<string> Merge(List<string> defaults, List<string> overrides)
    {
        if (overrides.Count == 0) return [.. defaults];
        return defaults.Count == 0 ? overrides : defaults.Union(overrides).ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public class ReviewConfig
{
    public string? Strategy { get; init; }
    public int? MaxComments { get; init; }
    public bool? EnableWorkItems { get; init; }
}

public class FileConfig
{
    public List<string> AllowedExtensions { get; init; } = [];
    public List<string> Ignore { get; init; } = [];

    public bool ShouldInclude(string path)
    {
        var extension = Path.GetExtension(path);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        if (Ignore.Count == 0)
            return true;

        var matcher = new Matcher();
        matcher.AddIncludePatterns(Ignore);
        return !matcher.Match("/", path.TrimStart('/')).HasMatches;
    }
}
