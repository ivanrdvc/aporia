using System.Runtime.Serialization;
using System.Text.Json.Serialization;

using Revu.Git;

namespace Revu;

public record ReviewRequest(
    GitProvider Provider,
    string Project,
    string RepositoryId,
    string RepositoryName,
    int PullRequestId,
    string SourceBranch,
    string TargetBranch,
    string Organization = ""
)
{
    public string ConversationId => $"pr-{RepositoryId}-{PullRequestId}";
}

public record Diff(List<FileChange> Files, string? Cursor = null);

public record PrContext(string Title, string? Description, IReadOnlyList<string> CommitMessages);

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
);

public record ReviewResult(
    List<Finding> Findings,
    string Summary
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
