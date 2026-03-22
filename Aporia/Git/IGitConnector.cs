using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Aporia.Git;

public interface IGitConnector
{
    Task<ProjectConfig> GetConfig(ReviewRequest req);
    Task<Diff> GetDiff(ReviewRequest req, ProjectConfig config);
    Task PostReview(ReviewRequest req, Diff diff, ReviewResult result);

    Task<string?> GetFile(ReviewRequest req, string path);
    Task<IReadOnlyList<string>> ListFiles(ReviewRequest req, string path, bool recursive = false);
    Task<IReadOnlyList<SearchResult>> SearchCode(ReviewRequest req, string query);
    Task<PrContext> GetPrContext(ReviewRequest req);
    Task<(string Source, string Target)?> GetPrBranches(ReviewRequest req);
    Task<ChatThreadContext?> GetChatThreadContext(ChatRequest req);
    Task PostChatReply(ChatRequest req, string body);

    /// <summary>
    /// Returns a clone URL and auth token for strategies that operate on a local checkout.
    /// </summary>
    Task<CloneCredentials> GetCloneCredentials(ReviewRequest req);
}

public record CloneCredentials(string Url, string Token);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind { Add, Edit, Delete, Rename }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitProvider
{
    [EnumMember(Value = "ado")]
    Ado,

    [EnumMember(Value = "github")]
    GitHub
}

public record SearchResult(string Path, int Line, string Snippet);
