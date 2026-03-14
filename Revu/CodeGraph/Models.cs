using Newtonsoft.Json;

using Revu.Git;

namespace Revu.CodeGraph;

public record IndexRequest(string RepositoryId, string Project, GitProvider Provider, string Branch);

public class FileIndex
{
    [JsonProperty("id")]
    public string Id { get; init; } = null!;

    [JsonProperty("repoId")]
    public string RepoId { get; init; } = null!;

    [JsonProperty("branch")]
    public string Branch { get; init; } = null!;

    [JsonProperty("language")]
    public string Language { get; init; } = null!;

    [JsonProperty("hash")]
    public string ContentHash { get; init; } = null!;

    [JsonProperty("symbols")]
    public List<SymbolNode> Symbols { get; init; } = [];

    [JsonProperty("refs")]
    public List<SymbolReference> References { get; init; } = [];
}

public class SymbolNode
{
    [JsonProperty("name")]
    public string Name { get; init; } = null!;

    [JsonProperty("kind")]
    public string Kind { get; init; } = null!;

    [JsonProperty("startLine")]
    public int StartLine { get; init; }

    [JsonProperty("endLine")]
    public int EndLine { get; init; }

    [JsonProperty("sig")]
    public string Signature { get; init; } = null!;

    [JsonProperty("enclosing")]
    public string? Enclosing { get; init; }
}

public class SymbolReference
{
    [JsonProperty("target")]
    public string Target { get; init; } = null!;

    [JsonProperty("kind")]
    public string Kind { get; init; } = null!;

    [JsonProperty("line")]
    public int? Line { get; init; }
}
