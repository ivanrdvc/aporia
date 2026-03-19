using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

using Aporia.Git;

namespace Aporia;

public record ReviewRequest(
    GitProvider Provider,
    string Project,
    string RepositoryId,
    string RepositoryName,
    int PullRequestId,
    string SourceBranch,
    string TargetBranch,
    string Organization = "",
    long? InstallationId = null
)
{
    public string ConversationId => $"pr-{RepositoryId}-{PullRequestId}";
}

public record Diff(List<FileChange> Files, string? Cursor = null);

public record PrContext(string Title, string? Description, IReadOnlyList<string> CommitMessages, IReadOnlyList<WorkItemContext>? WorkItems = null);

public record WorkItemContext(
    string Type,
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    WorkItemContext? Parent
);

public record FileChange(
    string Path,
    ChangeKind Kind,
    string? Patch,
    string? Content = null,
    string? OldPath = null
);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity
{
    [EnumMember(Value = "critical")]
    Critical,

    [EnumMember(Value = "warning")]
    Warning,

    [EnumMember(Value = "info")]
    Info
}

public record Finding(
    string FilePath,
    int StartLine,
    int? EndLine,
    Severity Severity,
    string Message,
    string? CodeFix = null
)
{
    public static string Fingerprint(Finding finding)
    {
        var input = $"{finding.FilePath.TrimStart('/').ToLowerInvariant()}|{finding.Message.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..16];
    }
}

public record ReviewResult(
    List<Finding> Findings,
    string Summary
);

public record ChatRequest(
    ReviewRequest Review,
    int ThreadId,
    int CommentId,
    string UserMessage
)
{
    public const string MarkerPrefix = "<!-- aporia:";
    public const string ChatMarker = "<!-- aporia:chat -->";
    public const string ReviewMarker = "<!-- aporia:review -->";
}

public record ChatThreadContext(
    int ThreadId,
    string? Fingerprint,
    string? FilePath,
    int? StartLine,
    IReadOnlyList<string> ThreadMessages
);

public record ExplorationResult(
    string Answer,
    List<FileEvidence> Evidence
);

public record FileEvidence(
    string FilePath,
    int? Line,
    string Snippet
);
